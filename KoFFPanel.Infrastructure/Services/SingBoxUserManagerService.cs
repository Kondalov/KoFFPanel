using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class SingBoxUserManagerService : ISingBoxUserManagerService
{
    private readonly IAppLogger _logger;
    private readonly AppDbContext _dbContext;

    public SingBoxUserManagerService(IAppLogger logger, AppDbContext dbContext)
    {
        _logger = logger;
        _dbContext = dbContext;
    }

    public async Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp)
    {
        var users = new List<VpnClient>();
        if (!ssh.IsConnected) return users;

        string rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json 2>/dev/null");
        if (string.IsNullOrWhiteSpace(rawJson) || !rawJson.Contains("{")) return users;

        try
        {
            var root = JsonNode.Parse(rawJson);
            var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();

            var tlsNode = root?["inbounds"]?[0]?["tls"];
            string pubKey = tlsNode?["reality"]?["public_key"]?.ToString().Trim() ?? "";
            string sni = tlsNode?["server_name"]?.ToString().Trim() ?? "www.microsoft.com";
            string shortId = tlsNode?["reality"]?["short_id"]?[0]?.ToString().Trim() ?? "";

            string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
            var clientsArray = root?["inbounds"]?[0]?["users"]?.AsArray() ?? new JsonArray();

            foreach (var dbUser in dbUsers)
            {
                var sbUser = clientsArray.FirstOrDefault(c => c?["name"]?.ToString().Trim() == dbUser.Email);
                dbUser.IsActive = (sbUser != null);
                dbUser.VlessLink = $"vless://{dbUser.Uuid}@{safeIp}:443?type=tcp&security=reality&pbk={pubKey}&fp=chrome&sni={sni}&sid={shortId}&spx=%2F&flow=xtls-rprx-vision#SingBox_{dbUser.Email}";
                users.Add(dbUser);
            }

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
                        VlessLink = $"vless://{uuid}@{safeIp}:443?type=tcp&security=reality&pbk={pubKey}&fp=chrome&sni={sni}&sid={shortId}&spx=%2F&flow=xtls-rprx-vision#SingBox_{name}"
                    };
                    _dbContext.Clients.Add(newUser); users.Add(newUser); dbChanged = true;
                }
            }
            if (dbChanged) await _dbContext.SaveChangesAsync();
        }
        catch (Exception ex) { _logger.Log("SB-USER-MGR", $"Ошибка парсинга: {ex.Message}"); }

        return users;
    }

    public async Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string name, long trafficLimitBytes, DateTime? expiryDate)
    {
        try
        {
            string rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json");
            var root = JsonNode.Parse(rawJson);
            var clientsArray = root?["inbounds"]?[0]?["users"]?.AsArray();
            if (clientsArray == null) return (false, "Структура JSON повреждена.", "");

            if (clientsArray.Any(c => c?["name"]?.ToString().Trim() == name)) return (false, $"Пользователь {name} уже существует!", "");

            string newUuid = Guid.NewGuid().ToString();
            clientsArray.Add(new JsonObject { ["name"] = name, ["uuid"] = newUuid, ["flow"] = "xtls-rprx-vision" });

            var tlsNode = root?["inbounds"]?[0]?["tls"];
            string pubKey = tlsNode?["reality"]?["public_key"]?.ToString().Trim() ?? "";
            string sni = tlsNode?["server_name"]?.ToString().Trim() ?? "www.microsoft.com";
            string shortId = tlsNode?["reality"]?["short_id"]?[0]?.ToString().Trim() ?? "";

            string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
            string vlessLink = $"vless://{newUuid}@{safeIp}:443?type=tcp&security=reality&pbk={pubKey}&fp=chrome&sni={sni}&sid={shortId}&spx=%2F&flow=xtls-rprx-vision#SingBox_{name}";

            var result = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (result.IsSuccess)
            {
                _dbContext.Clients.Add(new VpnClient { Email = name, Uuid = newUuid, ServerIp = serverIp, TrafficLimit = trafficLimitBytes, ExpiryDate = expiryDate, VlessLink = vlessLink });
                await _dbContext.SaveChangesAsync();
            }
            return (result.IsSuccess, result.Message, vlessLink);
        }
        catch (Exception ex) { return (false, $"Ошибка: {ex.Message}", ""); }
    }

    public async Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string name)
    {
        try
        {
            bool removedFromCore = false;
            string rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json 2>/dev/null");
            if (!string.IsNullOrWhiteSpace(rawJson) && rawJson.Contains("{"))
            {
                var root = JsonNode.Parse(rawJson);
                var clientsArray = root?["inbounds"]?[0]?["users"]?.AsArray();
                if (clientsArray != null)
                {
                    var userNode = clientsArray.FirstOrDefault(c => c?["name"]?.ToString().Trim() == name);
                    if (userNode != null)
                    {
                        if (clientsArray.Count <= 1) return (false, "Защита: Нельзя удалить последнего пользователя!");
                        clientsArray.Remove(userNode);
                        var result = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                        if (!result.IsSuccess) return result;
                        removedFromCore = true;
                    }
                }
            }

            var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
            if (dbUser != null) { _dbContext.Clients.Remove(dbUser); await _dbContext.SaveChangesAsync(); }

            return (true, removedFromCore ? $"Пользователь {name} удален." : $"Пользователь {name} вычищен из БД.");
        }
        catch (Exception ex) { return (false, $"Ошибка удаления: {ex.Message}"); }
    }

    public async Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string name, bool enableAccess)
    {
        try
        {
            var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
            if (dbUser == null) return (false, "Не найден в БД.");

            string rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json");
            var root = JsonNode.Parse(rawJson);
            var clientsArray = root?["inbounds"]?[0]?["users"]?.AsArray();
            if (clientsArray == null) return (false, "Ошибка JSON.");

            var userNode = clientsArray.FirstOrDefault(c => c?["name"]?.ToString().Trim() == name);

            if (enableAccess)
            {
                if (userNode != null) return (true, "Уже активен.");
                clientsArray.Add(new JsonObject { ["name"] = name, ["uuid"] = dbUser.Uuid, ["flow"] = "xtls-rprx-vision" });
            }
            else
            {
                if (userNode == null) return (true, "Уже заблокирован.");
                if (clientsArray.Count <= 1) return (false, "Нельзя заблокировать последнего пользователя.");
                clientsArray.Remove(userNode);
            }

            var result = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (result.IsSuccess) { dbUser.IsActive = enableAccess; await _dbContext.SaveChangesAsync(); }
            return result;
        }
        catch (Exception ex) { return (false, $"Ошибка статуса: {ex.Message}"); }
    }

    private async Task<(bool IsSuccess, string Message)> ApplyAndTestConfigAsync(ISshService ssh, string newJson)
    {
        string base64Json = Convert.ToBase64String(Encoding.UTF8.GetBytes(newJson.Replace("\r", "")));
        await ssh.ExecuteCommandAsync($"echo '{base64Json}' | base64 -d > /tmp/sb_test.json");

        var testResult = await ssh.ExecuteCommandAsync("/usr/local/bin/sing-box check -c /tmp/sb_test.json 2>&1");
        if (testResult.Contains("FATAL") || testResult.Contains("error")) return (false, "ОШИБКА: Конфиг не прошел тест Sing-box!");

        await ssh.ExecuteCommandAsync("cp /etc/sing-box/config.json /etc/sing-box/config.backup.json");
        await ssh.ExecuteCommandAsync("mv /tmp/sb_test.json /etc/sing-box/config.json");
        await ssh.ExecuteCommandAsync("systemctl restart sing-box");

        return (true, "Пользователи обновлены!");
    }

    public async Task<bool> UpdateUserLimitsAsync(string serverIp, string name, long newLimitBytes, DateTime? newExpiryDate)
    {
        var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
        if (dbUser == null) return false;
        dbUser.TrafficLimit = newLimitBytes; dbUser.ExpiryDate = newExpiryDate;
        await _dbContext.SaveChangesAsync(); return true;
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
            var clientsArray = root?["inbounds"]?[0]?["users"]?.AsArray();

            if (clientsArray == null) return false;

            clientsArray.Clear(); // Очищаем дефолтных
            foreach (var user in dbUsers.Where(u => u.IsActive))
            {
                clientsArray.Add(new JsonObject { ["name"] = user.Email, ["uuid"] = user.Uuid, ["flow"] = "xtls-rprx-vision" });
            }

            string updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            string base64Json = Convert.ToBase64String(Encoding.UTF8.GetBytes(updatedJson.Replace("\r", "")));

            await ssh.ExecuteCommandAsync($"echo '{base64Json}' | base64 -d > /etc/sing-box/config.json");
            await ssh.ExecuteCommandAsync("systemctl restart sing-box");

            _logger.Log("USER-SYNC", $"Успешно синхронизировано {dbUsers.Count(u => u.IsActive)} юзеров с ядром Sing-box.");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log("USER-SYNC", $"Ошибка синхронизации Sing-box: {ex.Message}");
            return false;
        }
    }

    // УМНОЕ РЕШЕНИЕ: Sing-box не поддерживает сбор статистики трафика. 
    // Возвращаем пустой словарь мгновенно, чтобы цикл мониторинга летал!
    public async Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh)
    {
        await Task.CompletedTask;
        return new Dictionary<string, long>();
    }

    public async Task<bool> ResetTrafficAsync(ISshService ssh, string name)
    {
        await Task.CompletedTask;
        return true;
    }
}