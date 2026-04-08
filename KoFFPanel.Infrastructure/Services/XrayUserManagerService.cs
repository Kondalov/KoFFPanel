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

    public async Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp)
    {
        var users = new List<VpnClient>();
        if (!ssh.IsConnected) return users;

        string rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null");
        if (string.IsNullOrWhiteSpace(rawJson) || rawJson.Contains("No such file")) return users;

        try
        {
            var root = JsonNode.Parse(rawJson);
            if (root == null) return users;

            var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();

            var realitySettings = root["inbounds"]?[0]?["streamSettings"]?["realitySettings"];
            string pubKey = "";
            string privateKey = realitySettings?["privateKey"]?.ToString().Trim() ?? "";
            string sni = realitySettings?["serverNames"]?[0]?.ToString().Trim() ?? "";
            string shortId = realitySettings?["shortIds"]?[0]?.ToString().Trim() ?? "";

            if (!string.IsNullOrEmpty(privateKey))
            {
                var keyOutput = await ssh.ExecuteCommandAsync($"/usr/local/bin/xray x25519 -i {privateKey}");
                var pubMatch = System.Text.RegularExpressions.Regex.Match(keyOutput, @"(?i)(?:Public\s*key|PublicKey|Password\s*\(PublicKey\))\s*:\s*(\S+)");
                if (pubMatch.Success) pubKey = pubMatch.Groups[1].Value.Trim();
            }

            string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
            var clientsArray = root["inbounds"]?[0]?["settings"]?["clients"]?.AsArray() ?? new System.Text.Json.Nodes.JsonArray();

            foreach (var dbUser in dbUsers)
            {
                var xrayUser = clientsArray.FirstOrDefault(c => c?["email"]?.ToString().Trim() == dbUser.Email);
                dbUser.IsActive = (xrayUser != null);

                dbUser.VlessLink = $"vless://{dbUser.Uuid}@{safeIp}:443?security=reality&encryption=none&pbk={pubKey}&headerType=none&fp=chrome&type=tcp&flow=xtls-rprx-vision&sni={sni}&sid={shortId}#KoFFPanel-{dbUser.Email}";

                users.Add(dbUser);
            }

            bool dbChanged = false;
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
                        VlessLink = $"vless://{uuid}@{safeIp}:443?security=reality&encryption=none&pbk={pubKey}&headerType=none&fp=chrome&type=tcp&flow=xtls-rprx-vision&sni={sni}&sid={shortId}#KoFFPanel-{email}"
                    };
                    _dbContext.Clients.Add(newUser);
                    users.Add(newUser);
                    dbChanged = true;
                }
            }

            if (dbChanged) await _dbContext.SaveChangesAsync();
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
            string rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json");
            var root = JsonNode.Parse(rawJson);
            if (root == null) return (false, "Ошибка чтения JSON.", "");

            var clientsArray = root["inbounds"]?[0]?["settings"]?["clients"]?.AsArray();
            if (clientsArray == null) return (false, "Структура повреждена.", "");

            foreach (var c in clientsArray)
            {
                if (c != null && c["email"]?.ToString().Trim() == email)
                    return (false, $"Пользователь {email} уже существует!", "");
            }

            string newUuid = Guid.NewGuid().ToString();
            clientsArray.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = newUuid, ["flow"] = "xtls-rprx-vision", ["email"] = email });

            var realitySettings = root["inbounds"]?[0]?["streamSettings"]?["realitySettings"];
            string privateKey = realitySettings?["privateKey"]?.ToString().Trim() ?? "";
            string sni = realitySettings?["serverNames"]?[0]?.ToString().Trim() ?? "";
            string shortId = realitySettings?["shortIds"]?[0]?.ToString().Trim() ?? "";

            string pubKey = "";
            if (!string.IsNullOrEmpty(privateKey))
            {
                var keyOutput = await ssh.ExecuteCommandAsync($"/usr/local/bin/xray x25519 -i {privateKey}");
                var pubMatch = System.Text.RegularExpressions.Regex.Match(keyOutput, @"(?i)(?:Public\s*key|PublicKey|Password\s*\(PublicKey\))\s*:\s*(\S+)");
                if (pubMatch.Success) pubKey = pubMatch.Groups[1].Value.Trim();
            }

            string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
            string vlessLink = $"vless://{newUuid}@{safeIp}:443?security=reality&encryption=none&pbk={pubKey}&headerType=none&fp=chrome&type=tcp&flow=xtls-rprx-vision&sni={sni}&sid={shortId}#KoFFPanel-{email}";

            string updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var result = await ApplyAndTestConfigAsync(ssh, updatedJson);

            if (result.IsSuccess)
            {
                _dbContext.Clients.Add(new VpnClient
                {
                    Email = email,
                    Uuid = newUuid,
                    ServerIp = serverIp,
                    TrafficLimit = trafficLimitBytes,
                    ExpiryDate = expiryDate,
                    VlessLink = vlessLink
                });
                await _dbContext.SaveChangesAsync();
            }

            return (result.IsSuccess, result.Message, vlessLink);
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка: {ex.Message}", "");
        }
    }

    public async Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string email)
    {
        try
        {
            bool removedFromXray = false;
            string xrayMessage = "";

            string rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null");
            if (!string.IsNullOrWhiteSpace(rawJson) && rawJson.Contains("{"))
            {
                var root = JsonNode.Parse(rawJson);
                var clientsArray = root?["inbounds"]?[0]?["settings"]?["clients"]?.AsArray();

                if (clientsArray != null)
                {
                    int indexToRemove = -1;
                    for (int i = 0; i < clientsArray.Count; i++)
                    {
                        if (clientsArray[i]?["email"]?.ToString().Trim() == email)
                        {
                            indexToRemove = i;
                            break;
                        }
                    }

                    if (indexToRemove != -1)
                    {
                        if (clientsArray.Count <= 1) return (false, "Нельзя удалить единственного (административного) пользователя сервера!");

                        clientsArray.RemoveAt(indexToRemove);
                        string updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                        var result = await ApplyAndTestConfigAsync(ssh, updatedJson);

                        if (!result.IsSuccess) return result;
                        removedFromXray = true;
                    }
                    else
                    {
                        xrayMessage = " (в ядре его уже не было)";
                    }
                }
            }

            var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
            if (dbUser != null)
            {
                _dbContext.Clients.Remove(dbUser);
                await _dbContext.SaveChangesAsync();
            }

            if (removedFromXray)
                return (true, $"Пользователь {email} полностью удален.");
            else
                return (true, $"Пользователь {email} очищен из БД{xrayMessage}.");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка удаления: {ex.Message}");
        }
    }

    private async Task<(bool IsSuccess, string Message)> ApplyAndTestConfigAsync(ISshService ssh, string newJson)
    {
        string base64Json = Convert.ToBase64String(Encoding.UTF8.GetBytes(newJson));
        await ssh.ExecuteCommandAsync($"echo '{base64Json}' | base64 -d > /tmp/config_users_test.json");

        var testResult = await ssh.ExecuteCommandAsync("/usr/local/bin/xray run -test -config /tmp/config_users_test.json");
        if (!testResult.Contains("Configuration OK"))
        {
            return (false, "ОШИБКА: Сгенерированный конфиг не прошел тест Xray!");
        }

        await ssh.ExecuteCommandAsync("cp /usr/local/etc/xray/config.json /usr/local/etc/xray/config.backup.json");
        await ssh.ExecuteCommandAsync("mv /tmp/config_users_test.json /usr/local/etc/xray/config.json");
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

            if (string.IsNullOrWhiteSpace(apiResponse) || !apiResponse.Contains("stat"))
                return stats;

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

                        if (stats.ContainsKey(email))
                            stats[email] += value;
                        else
                            stats[email] = value;
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

            string rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json");
            var root = JsonNode.Parse(rawJson);
            var clientsArray = root?["inbounds"]?[0]?["settings"]?["clients"]?.AsArray();
            if (clientsArray == null) return (false, "Ошибка чтения JSON.");

            int existingIndex = -1;
            for (int i = 0; i < clientsArray.Count; i++)
            {
                if (clientsArray[i]?["email"]?.ToString().Trim() == email) { existingIndex = i; break; }
            }

            if (enableAccess)
            {
                if (existingIndex != -1) return (true, "Пользователь уже активен.");
                clientsArray.Add(new System.Text.Json.Nodes.JsonObject { ["id"] = dbUser.Uuid, ["flow"] = "xtls-rprx-vision", ["email"] = email });
            }
            else
            {
                if (existingIndex == -1) return (true, "Пользователь уже заблокирован.");
                if (clientsArray.Count <= 1) return (false, "Защита: нельзя заблокировать единственного (административного) пользователя сервера.");
                clientsArray.RemoveAt(existingIndex);
            }

            string updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            var result = await ApplyAndTestConfigAsync(ssh, updatedJson);

            if (result.IsSuccess)
            {
                dbUser.IsActive = enableAccess;
                await _dbContext.SaveChangesAsync();
            }

            return result;
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
}