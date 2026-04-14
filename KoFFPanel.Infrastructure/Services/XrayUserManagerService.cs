using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Linq;

namespace KoFFPanel.Infrastructure.Services;

public class XrayUserManagerService : IXrayUserManagerService
{
    private readonly IAppLogger _logger;
    private readonly AppDbContext _dbContext;

    public XrayUserManagerService(IAppLogger logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    private async Task RebuildInboundsAsync(JsonNode root, string serverIp, ISshService ssh)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        var inbounds = root?["inbounds"]?.AsArray();
        if (inbounds == null) return;

        bool hasReality = false;
        bool hasTrustTunnel = false;

        foreach (var inboundNode in inbounds)
        {
            var inbound = inboundNode as JsonObject;
            if (inbound == null) continue;

            if (inbound["protocol"]?.ToString() == "vless")
            {
                var network = inbound["streamSettings"]?["network"]?.ToString();
                // ИСПРАВЛЕНИЕ: Читаем новый формат xhttp для Xray
                if (network == "quic" || network == "xhttp") hasTrustTunnel = true;
                else if (network == "tcp") hasReality = true;
            }
        }

        foreach (var inboundNode in inbounds)
        {
            var inbound = inboundNode as JsonObject;
            if (inbound == null) continue;

            if (inbound["protocol"]?.ToString() == "vless")
            {
                var network = inbound["streamSettings"]?["network"]?.ToString();
                var isQuic = network == "quic" || network == "xhttp";

                var clientsArray = new JsonArray();
                foreach (var u in dbUsers)
                {
                    if (u.IsActive && ((!isQuic && u.IsVlessEnabled) || (isQuic && u.IsTrustTunnelEnabled)))
                    {
                        if (isQuic)
                            clientsArray.Add(new JsonObject { ["id"] = u.Uuid, ["email"] = u.Email });
                        else
                            clientsArray.Add(new JsonObject { ["id"] = u.Uuid, ["flow"] = "xtls-rprx-vision", ["email"] = u.Email });
                    }
                }

                if (inbound["settings"] is JsonObject settingsObj)
                {
                    settingsObj["clients"] = clientsArray;
                }

                int port = (int?)inbound["port"] ?? (isQuic ? 4433 : 443);
                string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;

                if (!isQuic)
                {
                    var realitySettings = inbound["streamSettings"]?["realitySettings"];
                    string pubKey = "";
                    string shortId = realitySettings?["shortIds"]?[0]?.ToString().Trim() ?? "";
                    string sni = realitySettings?["serverNames"]?[0]?.ToString().Trim() ?? "www.microsoft.com";
                    string privateKey = realitySettings?["privateKey"]?.ToString().Trim() ?? "";

                    if (!string.IsNullOrEmpty(privateKey))
                    {
                        var keyOutput = await ssh.ExecuteCommandAsync($"/usr/local/bin/xray x25519 -i {privateKey}");
                        var pubMatch = System.Text.RegularExpressions.Regex.Match(keyOutput, @"(?i)(?:Public\s*key|PublicKey|Password\s*\(PublicKey\))\s*:\s*(\S+)");
                        if (pubMatch.Success) pubKey = pubMatch.Groups[1].Value.Trim();
                    }

                    foreach (var u in dbUsers)
                    {
                        u.VlessLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=tcp&security=reality&encryption=none&pbk={pubKey}&headerType=none&fp=chrome&sni={sni}&sid={shortId}&flow=xtls-rprx-vision#Xray_{u.Email}";
                    }
                }
                else
                {
                    // ИСПРАВЛЕНИЕ: Формируем правильную ссылку XHTTP для ядра Xray
                    var tlsSettings = inbound["streamSettings"]?["tlsSettings"];
                    string sni = tlsSettings?["serverName"]?.ToString().Trim() ?? "adguard.com";

                    foreach (var u in dbUsers)
                    {
                        u.TrustTunnelLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=xhttp&security=tls&encryption=none&sni={sni}&alpn=h3&host={sni}&path=%2F&allowInsecure=1&insecure=1#TrustTunnel_{u.Email}";
                    }
                }
            }
        }

        if (!hasReality)
        {
            foreach (var u in dbUsers) { u.VlessLink = "VLESS не установлен на сервере!"; }
        }
        if (!hasTrustTunnel)
        {
            foreach (var u in dbUsers) { u.TrustTunnelLink = "TrustTunnel не установлен на сервере!"; }
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp)
    {
        var users = new List<VpnClient>();
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        if (dbUsers.Any()) users = dbUsers;

        if (!ssh.IsConnected) return users;

        string rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null");
        if (string.IsNullOrWhiteSpace(rawJson) || rawJson.Contains("No such file")) return users;

        try
        {
            var root = JsonNode.Parse(rawJson);
            if (root == null) return users;

            // Синхронизируем юзеров
            var inbounds = root["inbounds"]?.AsArray();
            if (inbounds != null)
            {
                bool dbChanged = false;
                foreach (var inboundNode in inbounds)
                {
                    var inbound = inboundNode as JsonObject;
                    if (inbound != null && inbound["protocol"]?.ToString() == "vless")
                    {
                        var clientsArray = inbound["settings"]?["clients"]?.AsArray() ?? new JsonArray();
                        foreach (var c in clientsArray)
                        {
                            string email = c?["email"]?.ToString().Trim() ?? "Unknown";
                            string uuid = c?["id"]?.ToString().Trim() ?? "";

                            if (!dbUsers.Any(u => u.Email == email))
                            {
                                var newUser = new VpnClient
                                {
                                    Email = email,
                                    Uuid = uuid,
                                    ServerIp = serverIp,
                                    Protocol = "VLESS",
                                    IsActive = true,
                                    TrafficUsed = 0,
                                    IsP2PBlocked = true,
                                    IsVlessEnabled = true,
                                    IsHysteria2Enabled = false,
                                    IsTrustTunnelEnabled = false
                                };
                                _dbContext.Clients.Add(newUser); dbUsers.Add(newUser); dbChanged = true;
                            }
                        }
                    }
                }
                if (dbChanged) await _dbContext.SaveChangesAsync();
            }

            await RebuildInboundsAsync(root, serverIp, ssh);
            users = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        }
        catch (Exception ex)
        {
            _logger.Log("USER-MGR", $"Ошибка парсинга: {ex.Message}");
        }

        return users;
    }

    public async Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string email, long trafficLimitBytes, DateTime? expiryDate)
    {
        try
        {
            var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.Email == email && c.ServerIp == serverIp);
            if (dbUser != null) return (false, $"Пользователь {email} уже существует!", "");

            string newUuid = Guid.NewGuid().ToString();
            var newUser = new VpnClient
            {
                Email = email,
                Uuid = newUuid,
                ServerIp = serverIp,
                TrafficLimit = trafficLimitBytes,
                ExpiryDate = expiryDate,
                IsP2PBlocked = true,
                IsVlessEnabled = true,
                IsHysteria2Enabled = false,
                IsTrustTunnelEnabled = false
            };

            _dbContext.Clients.Add(newUser);
            await _dbContext.SaveChangesAsync();

            string rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json");
            var root = JsonNode.Parse(rawJson);

            await RebuildInboundsAsync(root, serverIp, ssh);

            string updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var result = await ApplyAndTestConfigAsync(ssh, updatedJson);

            var savedUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.Email == email && c.ServerIp == serverIp);
            return (result.IsSuccess, result.Message, savedUser?.VlessLink ?? "");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка: {ex.Message}", "");
        }
    }

    public async Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string email)
    {
        if (!ssh.IsConnected) return (false, "Нет SSH подключения.");
        if (string.IsNullOrWhiteSpace(email)) return (false, "Email не может быть пустым.");
        if (email.Equals("Админ", StringComparison.OrdinalIgnoreCase)) return (false, "Критическая защита: Нельзя удалить системный профиль 'Админ'.");

        try
        {
            var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
            int count = await _dbContext.Clients.CountAsync(c => c.ServerIp == serverIp);
            if (count <= 1 && dbUser != null) return (false, "Защита Xray: Нельзя удалить последнего пользователя!");

            if (dbUser != null)
            {
                _dbContext.Clients.Remove(dbUser);
                var trafficLogs = await _dbContext.TrafficLogs.Where(t => t.ServerIp == serverIp && t.Email == email).ToListAsync();
                if (trafficLogs.Any()) _dbContext.TrafficLogs.RemoveRange(trafficLogs);
                var connLogs = await _dbContext.ConnectionLogs.Where(c => c.ServerIp == serverIp && c.Email == email).ToListAsync();
                if (connLogs.Any()) _dbContext.ConnectionLogs.RemoveRange(connLogs);
                var violationLogs = await _dbContext.ViolationLogs.Where(v => v.ServerIp == serverIp && v.Email == email).ToListAsync();
                if (violationLogs.Any()) _dbContext.ViolationLogs.RemoveRange(violationLogs);
                await _dbContext.SaveChangesAsync();
            }

            string rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null");
            if (string.IsNullOrWhiteSpace(rawJson) || !rawJson.Contains("{")) return (true, $"Пользователь {email} вычищен.");

            var root = JsonNode.Parse(rawJson);
            await RebuildInboundsAsync(root, serverIp, ssh);

            string updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            return await ApplyAndTestConfigAsync(ssh, updatedJson);
        }
        catch (Exception ex)
        {
            _logger.Log("USER-MGR", $"Критическая ошибка при удалении: {ex.Message}");
            return (false, $"Системная ошибка при удалении: {ex.Message}");
        }
    }

    private async Task<(bool IsSuccess, string Message)> ApplyAndTestConfigAsync(ISshService ssh, string newJson)
    {
        string base64Json = Convert.ToBase64String(Encoding.UTF8.GetBytes(newJson.Replace("\r", "")));
        await ssh.ExecuteCommandAsync($"echo '{base64Json}' | base64 -d > /tmp/config_users_test.json");

        var testResult = await ssh.ExecuteCommandAsync("/usr/local/bin/xray run -test -config /tmp/config_users_test.json 2>&1");
        if (!testResult.Contains("Configuration OK"))
        {
            return (false, "ОШИБКА: Сгенерированный конфиг не прошел тест Xray!");
        }

        await ssh.ExecuteCommandAsync("cp /usr/local/etc/xray/config.json /usr/local/etc/xray/config.backup.json");
        await ssh.ExecuteCommandAsync("mv /tmp/config_users_test.json /usr/local/etc/xray/config.json");

        // ИСПРАВЛЕНИЕ: Жестко убиваем Зомби-процессы Xray перед перезапуском
        string preRestartCleanup = @"
            systemctl stop xray 2>/dev/null || true
            killall -9 xray 2>/dev/null || true
        ".Replace("\r", "");
        await ssh.ExecuteCommandAsync(preRestartCleanup);

        await ssh.ExecuteCommandAsync("systemctl restart xray");

        return (true, "Пользователи успешно обновлены!");
    }

    public async Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh)
    {
        var stats = new Dictionary<string, long>();
        if (!ssh.IsConnected) return stats;

        try
        {
            string apiResponse = await ssh.ExecuteCommandAsync("/usr/local/bin/xray api statsquery --server=127.0.0.1:10085");
            if (string.IsNullOrWhiteSpace(apiResponse) || !apiResponse.Contains("stat")) return stats;

            var root = JsonNode.Parse(apiResponse);
            var statArray = root?["stat"]?.AsArray();

            if (statArray != null)
            {
                foreach (var item in statArray)
                {
                    string name = item?["name"]?.ToString() ?? "";
                    long value = long.Parse(item?["value"]?.ToString() ?? "0");

                    var parts = name.Split(">>>");
                    if (parts.Length == 4 && parts[0] == "user")
                    {
                        string email = parts[1];
                        if (stats.ContainsKey(email)) stats[email] += value;
                        else stats[email] = value;
                    }
                }
            }
        }
        catch { }

        return stats;
    }

    public async Task<bool> ResetTrafficAsync(ISshService ssh, string email)
    {
        if (!ssh.IsConnected) return false;
        try
        {
            string cmd = $"/usr/local/bin/xray api statsquery --server=127.0.0.1:10085 --pattern \"user>>>{email}>>>traffic\" --reset";
            await ssh.ExecuteCommandAsync(cmd);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log("USER-MGR", $"Ошибка сброса трафика для {email}: {ex.Message}");
            return false;
        }
    }

    public async Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string email, bool enableAccess)
    {
        try
        {
            var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
            if (dbUser == null) return (false, "Пользователь не найден в БД.");

            dbUser.IsActive = enableAccess;
            await _dbContext.SaveChangesAsync();

            string rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json");
            var root = JsonNode.Parse(rawJson);

            await RebuildInboundsAsync(root, serverIp, ssh);

            string updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            return await ApplyAndTestConfigAsync(ssh, updatedJson);
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка изменения статуса: {ex.Message}");
        }
    }

    public async Task<bool> UpdateUserLimitsAsync(string serverIp, string email, long newLimitBytes, DateTime? newExpiryDate)
    {
        try
        {
            var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
            if (dbUser == null) return false;

            dbUser.TrafficLimit = newLimitBytes;
            dbUser.ExpiryDate = newExpiryDate;

            await _dbContext.SaveChangesAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log("USER-MGR", $"Ошибка обновления лимитов: {ex.Message}");
            return false;
        }
    }

    public async Task SaveTrafficToDbAsync(string serverIp, IEnumerable<VpnClient> clients)
    {
        try
        {
            var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
            foreach (var client in clients)
            {
                var dbUser = dbUsers.FirstOrDefault(u => u.Email == client.Email);
                if (dbUser != null)
                {
                    dbUser.TrafficUsed = client.TrafficUsed;
                }
            }
            await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.Log("USER-MGR", $"Ошибка сохранения БД: {ex.Message}");
        }
    }

    public async Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> dbUsers)
    {
        try
        {
            string rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json");
            var root = JsonNode.Parse(rawJson);

            string serverIp = dbUsers.FirstOrDefault()?.ServerIp ?? "";
            if (!string.IsNullOrEmpty(serverIp))
            {
                await RebuildInboundsAsync(root, serverIp, ssh);
            }

            string updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var result = await ApplyAndTestConfigAsync(ssh, updatedJson);

            _logger.Log("USER-SYNC", $"Успешно синхронизировано {dbUsers.Count(u => u.IsActive)} юзеров с ядром Xray.");
            return result.IsSuccess;
        }
        catch (Exception ex)
        {
            _logger.Log("USER-SYNC", $"Ошибка синхронизации Xray: {ex.Message}");
            return false;
        }
    }
}