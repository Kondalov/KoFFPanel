using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class SingBoxUserManagerService : ISingBoxUserManagerService
{
    private readonly IAppLogger _logger;
    private readonly AppDbContext _dbContext;
    private readonly IProfileRepository _profileRepository;

    public SingBoxUserManagerService(IAppLogger logger, AppDbContext dbContext, IProfileRepository profileRepository)
    {
        _logger = logger;
        _dbContext = dbContext;
        _profileRepository = profileRepository;
    }

    private async Task RebuildInboundsAsync(JsonNode root, string serverIp)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        var inbounds = root?["inbounds"]?.AsArray();
        if (inbounds == null) return;

        bool hasReality = false;
        bool hasHy2 = false;
        bool hasTrustTunnel = false;

        // 1. Сначала сканируем сервер и узнаем, какие протоколы реально установлены
        foreach (var inboundNode in inbounds)
        {
            var inbound = inboundNode as JsonObject;
            if (inbound == null) continue;

            var type = inbound["type"]?.ToString();
            if (type == "vless")
            {
                if (inbound["transport"]?["type"]?.ToString() == "quic") hasTrustTunnel = true;
                else hasReality = true;
            }
            else if (type == "hysteria2") hasHy2 = true;
        }

        // === ИСПРАВЛЕНИЕ: Самоисцеление базы данных SQLite ===
        // Если протокол установлен, снимаем ошибочную блокировку тумблеров!
        foreach (var u in dbUsers)
        {
            if (hasReality || hasTrustTunnel) u.IsVlessEnabled = true;
            if (hasHy2) u.IsHysteria2Enabled = true;
        }

        // 2. Генерируем массив пользователей
        foreach (var inboundNode in inbounds)
        {
            var inbound = inboundNode as JsonObject;
            if (inbound == null) continue;

            var type = inbound["type"]?.ToString();

            if (type == "vless")
            {
                var isQuic = inbound["transport"]?["type"]?.ToString() == "quic";
                var usersArray = new JsonArray();

                foreach (var u in dbUsers)
                {
                    if (u.IsActive && u.IsVlessEnabled)
                    {
                        if (isQuic)
                            usersArray.Add(new JsonObject { ["name"] = u.Email, ["uuid"] = u.Uuid });
                        else
                            usersArray.Add(new JsonObject { ["name"] = u.Email, ["uuid"] = u.Uuid, ["flow"] = "xtls-rprx-vision" });
                    }
                }
                inbound["users"] = usersArray;

                if (!isQuic)
                {
                    string pubKey = "";
                    string shortId = "";
                    string sni = inbound["tls"]?["server_name"]?.ToString().Trim() ?? "www.microsoft.com";

                    try
                    {
                        var profile = _profileRepository.LoadProfiles().FirstOrDefault(p => p.IpAddress == serverIp);
                        var vlessInboundDb = profile?.Inbounds.FirstOrDefault(i => i.Protocol == "vless");
                        if (vlessInboundDb != null)
                        {
                            var settings = JsonDocument.Parse(vlessInboundDb.SettingsJson).RootElement;
                            pubKey = settings.GetProperty("publicKey").GetString() ?? "";
                            shortId = settings.GetProperty("shortId").GetString() ?? "";
                        }
                    }
                    catch { _logger.Log("SB-USER-MGR", "Не удалось извлечь publicKey из профиля."); }

                    string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
                    int port = (int?)inbound["listen_port"] ?? 443;

                    foreach (var u in dbUsers)
                    {
                        u.VlessLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=tcp&security=reality&pbk={pubKey}&fp=chrome&sni={sni}&sid={shortId}&spx=%2F&flow=xtls-rprx-vision#SingBox_{u.Email}";
                    }
                }
            }
            else if (type == "hysteria2")
            {
                var usersArray = new JsonArray();
                string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
                int port = (int?)inbound["listen_port"] ?? 8443;
                string sni = inbound["tls"]?["server_name"]?.ToString().Trim() ?? "www.microsoft.com";
                string obfsPassword = inbound["obfs"]?["salamander"]?["password"]?.ToString().Trim() ?? "";

                foreach (var u in dbUsers)
                {
                    if (u.IsActive && u.IsHysteria2Enabled)
                    {
                        usersArray.Add(new JsonObject { ["name"] = u.Email, ["password"] = u.Uuid });
                    }
                    string obfsParam = string.IsNullOrEmpty(obfsPassword) ? "" : $"&obfs=salamander&obfs-password={obfsPassword}";
                    u.Hysteria2Link = $"hy2://{u.Uuid}@{safeIp}:{port}?sni={sni}&insecure=1{obfsParam}#SingBox_HY2_{u.Email}";
                }
                inbound["users"] = usersArray;
            }
        }

        // Мягко информируем пользователя, не ломая тумблеры в БД
        if (!hasReality)
        {
            foreach (var u in dbUsers) { u.VlessLink = "VLESS не установлен на сервере!"; }
        }
        if (!hasHy2)
        {
            foreach (var u in dbUsers) { u.Hysteria2Link = "Hysteria 2 не установлен на сервере!"; }
        }

        await _dbContext.SaveChangesAsync();
    }

    private async Task ApplyP2PRulesAsync(JsonNode root, string serverIp)
    {
        try
        {
            var blockedNames = await _dbContext.Clients.AsNoTracking().Where(c => c.ServerIp == serverIp && c.IsP2PBlocked).Select(c => c.Email.Trim()).ToListAsync();

            if (root["log"] is JsonObject logObj) logObj["level"] = "trace";
            else if (root.AsObject() != null) root["log"] = new JsonObject { ["level"] = "trace" };

            var inbounds = root?["inbounds"]?.AsArray();
            if (inbounds != null)
            {
                foreach (var inbound in inbounds)
                {
                    if (inbound is JsonObject inboundObj)
                    {
                        inboundObj.Remove("sniff"); inboundObj.Remove("sniffing"); inboundObj.Remove("sniff_override_destination");
                    }
                }
            }

            string rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules");
            if (!Directory.Exists(rulesDir)) Directory.CreateDirectory(rulesDir);
            string rulesFile = Path.Combine(rulesDir, "torrent_domains.txt");
            List<string> domains;

            if (!File.Exists(rulesFile))
            {
                domains = new List<string> { "torrent", "tracker", "rutracker", "nnmclub", "kinozal", "rutor", "piratebay", "tapochek", "lostfilm" };
                await File.WriteAllLinesAsync(rulesFile, domains);
            }
            else
            {
                domains = (await File.ReadAllLinesAsync(rulesFile)).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
            }

            var rulesArray = root?["route"]?["rules"]?.AsArray();
            if (rulesArray != null)
            {
                var rulesToRemove = rulesArray.Where(r => r?["outbound"]?.ToString() == "block" || r?["action"]?.ToString() == "sniff").ToList();
                foreach (var r in rulesToRemove) rulesArray.Remove(r);

                if (blockedNames.Any())
                {
                    rulesArray.Insert(0, new JsonObject { ["action"] = "sniff" });
                    if (domains.Any()) rulesArray.Insert(1, new JsonObject { ["auth_user"] = JsonSerializer.SerializeToNode(blockedNames), ["domain_keyword"] = JsonSerializer.SerializeToNode(domains), ["outbound"] = "block" });
                    rulesArray.Insert(2, new JsonObject { ["auth_user"] = JsonSerializer.SerializeToNode(blockedNames), ["protocol"] = "bittorrent", ["outbound"] = "block" });
                }
            }
        }
        catch (Exception ex) { _logger.Log("SB-P2P-RULES", $"Ошибка: {ex.Message}"); }
    }

    public async Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp)
    {
        var users = new List<VpnClient>();
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        if (dbUsers.Any()) users = dbUsers;

        if (!ssh.IsConnected) return users;

        string rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json 2>/dev/null");
        if (string.IsNullOrWhiteSpace(rawJson) || !rawJson.Contains("{")) return users;

        try
        {
            var root = JsonNode.Parse(rawJson);

            var vlessInbound = root?["inbounds"]?.AsArray()?.FirstOrDefault(i => i?["type"]?.ToString() == "vless");
            if (vlessInbound != null)
            {
                var clientsArray = vlessInbound["users"]?.AsArray() ?? new JsonArray();
                bool dbChanged = false;
                foreach (var c in clientsArray)
                {
                    string name = c?["name"]?.ToString().Trim() ?? "Unknown";
                    string uuid = c?["uuid"]?.ToString().Trim() ?? "";

                    if (!dbUsers.Any(u => u.Email == name))
                    {
                        var newUser = new VpnClient
                        {
                            Email = name,
                            Uuid = uuid,
                            ServerIp = serverIp,
                            Protocol = "VLESS",
                            IsActive = true,
                            TrafficUsed = 0,
                            IsP2PBlocked = true,
                            IsVlessEnabled = true,
                            IsHysteria2Enabled = false
                        };
                        _dbContext.Clients.Add(newUser); dbUsers.Add(newUser); dbChanged = true;
                    }
                }
                if (dbChanged) await _dbContext.SaveChangesAsync();
            }

            await RebuildInboundsAsync(root, serverIp);
            users = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        }
        catch (Exception ex) { _logger.Log("SB-USER-MGR", $"Ошибка: {ex.Message}"); }

        return users;
    }

    public async Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string name, long trafficLimitBytes, DateTime? expiryDate, bool isP2PBlocked = true)
    {
        try
        {
            var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.Email == name && c.ServerIp == serverIp);
            if (dbUser != null) return (false, $"Пользователь {name} уже существует!", "");

            string newUuid = Guid.NewGuid().ToString();
            var newUser = new VpnClient
            {
                Email = name,
                Uuid = newUuid,
                ServerIp = serverIp,
                TrafficLimit = trafficLimitBytes,
                ExpiryDate = expiryDate,
                IsP2PBlocked = isP2PBlocked,
                IsVlessEnabled = true,
                IsHysteria2Enabled = false
            };

            _dbContext.Clients.Add(newUser);
            await _dbContext.SaveChangesAsync();

            string rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json");
            var root = JsonNode.Parse(rawJson);

            await RebuildInboundsAsync(root, serverIp);
            await ApplyP2PRulesAsync(root, serverIp);

            var result = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            var savedUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.Email == name && c.ServerIp == serverIp);
            return (result.IsSuccess, result.Message, savedUser?.VlessLink ?? "");
        }
        catch (Exception ex) { return (false, $"Ошибка: {ex.Message}", ""); }
    }

    public async Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string name)
    {
        try
        {
            var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
            int count = await _dbContext.Clients.CountAsync(c => c.ServerIp == serverIp);
            if (count <= 1 && dbUser != null) return (false, "Защита: Нельзя удалить последнего пользователя!");

            if (dbUser != null) { _dbContext.Clients.Remove(dbUser); await _dbContext.SaveChangesAsync(); }

            string rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json 2>/dev/null");
            if (string.IsNullOrWhiteSpace(rawJson) || !rawJson.Contains("{")) return (true, $"Пользователь {name} вычищен.");

            var root = JsonNode.Parse(rawJson);
            await RebuildInboundsAsync(root, serverIp);
            await ApplyP2PRulesAsync(root, serverIp);

            return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { return (false, $"Ошибка удаления: {ex.Message}"); }
    }

    public async Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string name, bool enableAccess)
    {
        try
        {
            var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
            if (dbUser == null) return (false, "Не найден в БД.");

            dbUser.IsActive = enableAccess;
            await _dbContext.SaveChangesAsync();

            string rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json");
            var root = JsonNode.Parse(rawJson);

            await RebuildInboundsAsync(root, serverIp);
            await ApplyP2PRulesAsync(root, serverIp);

            return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { return (false, $"Ошибка статуса: {ex.Message}"); }
    }

    public async Task<bool> UpdateUserLimitsAsync(ISshService ssh, string serverIp, string name, long newLimitBytes, DateTime? newExpiryDate, bool isP2PBlocked = true)
    {
        var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
        if (dbUser == null) return false;

        dbUser.TrafficLimit = newLimitBytes; dbUser.ExpiryDate = newExpiryDate; dbUser.IsP2PBlocked = isP2PBlocked;

        try
        {
            await _dbContext.SaveChangesAsync();
            string rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json");
            var root = JsonNode.Parse(rawJson);

            await RebuildInboundsAsync(root, serverIp);
            await ApplyP2PRulesAsync(root, serverIp);

            var result = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return result.IsSuccess;
        }
        catch (Exception ex) { _logger.Log("SB-USER-MGR", $"Ошибка: {ex.Message}"); return false; }
    }

    private async Task<(bool IsSuccess, string Message)> ApplyAndTestConfigAsync(ISshService ssh, string newJson)
    {
        _logger.Log("SB-FULL-CONFIG", $"Готовый конфиг для Sing-Box перед перезапуском:\n{newJson}");

        string base64Json = Convert.ToBase64String(Encoding.UTF8.GetBytes(newJson.Replace("\r", "")));
        await ssh.ExecuteCommandAsync($"echo '{base64Json}' | base64 -d > /tmp/sb_test.json");

        var testResult = await ssh.ExecuteCommandAsync("sing-box check -c /tmp/sb_test.json 2>&1");
        _logger.Log("SB-DIAGNOSTIC", $"Ответ от проверки sing-box check:\n{testResult.Trim()}");

        if (testResult.Contains("FATAL") || testResult.Contains("error"))
        {
            return (false, "ОШИБКА: Конфиг не прошел тест Sing-box!");
        }

        await ssh.ExecuteCommandAsync("cp /etc/sing-box/config.json /etc/sing-box/config.backup.json");
        await ssh.ExecuteCommandAsync("mv /tmp/sb_test.json /etc/sing-box/config.json");
        await ssh.ExecuteCommandAsync("systemctl restart sing-box");

        return (true, "Пользователи обновлены!");
    }

    public async Task SaveTrafficToDbAsync(string serverIp, IEnumerable<VpnClient> clients)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        foreach (var client in clients) { var dbUser = dbUsers.FirstOrDefault(u => u.Email == client.Email); if (dbUser != null) dbUser.TrafficUsed = client.TrafficUsed; }
        await _dbContext.SaveChangesAsync();
    }

    public async Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> dbUsers)
    {
        try
        {
            string rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json");
            var root = JsonNode.Parse(rawJson);

            string serverIp = dbUsers.FirstOrDefault()?.ServerIp ?? "";
            if (!string.IsNullOrEmpty(serverIp))
            {
                await RebuildInboundsAsync(root, serverIp);
                await ApplyP2PRulesAsync(root, serverIp);
            }

            var result = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            _logger.Log("USER-SYNC", $"Синхронизация Sing-box завершена: {result.Message}");
            return result.IsSuccess;
        }
        catch (Exception ex) { _logger.Log("USER-SYNC", $"Ошибка синхронизации: {ex.Message}"); return false; }
    }

    public async Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh)
    {
        await Task.CompletedTask; return new Dictionary<string, long>();
    }

    public async Task<bool> ResetTrafficAsync(ISshService ssh, string name)
    {
        await Task.CompletedTask; return true;
    }
}