using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public partial class XrayUserManagerService : IXrayUserManagerService
{
    private readonly IAppLogger _logger;
    private readonly AppDbContext _dbContext;

    public XrayUserManagerService(IAppLogger logger, AppDbContext dbContext)
    {
        _logger = logger; _dbContext = dbContext;
    }

    public async Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        if (!ssh.IsConnected) return dbUsers;
        string raw = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null");
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains("No such")) return dbUsers;
        try {
            var root = JsonNode.Parse(raw);
            foreach (var inbound in root?["inbounds"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()) {
                if (inbound["protocol"]?.ToString() == "vless") {
                    foreach (var c in inbound["settings"]?["clients"]?.AsArray() ?? new JsonArray()) {
                        string email = c?["email"]?.ToString() ?? "Unknown";
                        if (!dbUsers.Any(u => u.Email == email)) {
                            var newUser = new VpnClient { Email = email, Uuid = c?["id"]?.ToString() ?? "", ServerIp = serverIp, Protocol = "VLESS", IsActive = true, IsP2PBlocked = true, IsVlessEnabled = true };
                            _dbContext.Clients.Add(newUser); dbUsers.Add(newUser);
                        }
                    }
                }
            }
            await _dbContext.SaveChangesAsync(); await RebuildInboundsAsync(root, serverIp, ssh);
            return await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        } catch { return dbUsers; }
    }

    public async Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string email, long limit, DateTime? expiry)
    {
        try { SshGuard.ThrowIfInvalid(email, null); } catch (Exception ex) { return (false, ex.Message, ""); }
        if (await _dbContext.Clients.AnyAsync(c => c.Email == email && c.ServerIp == serverIp)) return (false, "Уже есть!", "");
        var newUser = new VpnClient { Email = email, Uuid = Guid.NewGuid().ToString(), ServerIp = serverIp, TrafficLimit = limit, ExpiryDate = expiry, IsP2PBlocked = true, IsVlessEnabled = true, IsActive = true };
        _dbContext.Clients.Add(newUser); await _dbContext.SaveChangesAsync();
        var root = JsonNode.Parse(await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json"));
        await RebuildInboundsAsync(root, serverIp, ssh);
        var res = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (res.IsSuccess, res.Message, (await _dbContext.Clients.FirstOrDefaultAsync(u => u.Email == email && u.ServerIp == serverIp))?.VlessLink ?? "");
    }

    public async Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string email)
    {
        if (email.Equals("Админ", StringComparison.OrdinalIgnoreCase)) return (false, "Нельзя удалить Админа!");
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
        if (await _dbContext.Clients.CountAsync(c => c.ServerIp == serverIp) <= 1) return (false, "Последний!");
        if (user != null) { _dbContext.Clients.Remove(user); await _dbContext.SaveChangesAsync(); }
        var root = JsonNode.Parse(await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null"));
        if (root == null) return (true, "Удален.");
        await RebuildInboundsAsync(root, serverIp, ssh);
        return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh)
    {
        var stats = new Dictionary<string, long>();
        try {
            var root = JsonNode.Parse(await ssh.ExecuteCommandAsync("/usr/local/bin/xray api statsquery --server=127.0.0.1:10085"));
            foreach (var item in root?["stat"]?.AsArray() ?? new JsonArray()) {
                var parts = item?["name"]?.ToString().Split(">>>");
                if (parts?.Length == 4 && parts[0] == "user") {
                    string email = parts[1]; long val = long.Parse(item?["value"]?.ToString() ?? "0");
                    if (stats.ContainsKey(email)) stats[email] += val; else stats[email] = val;
                }
            }
        } catch { }
        return stats;
    }

    public async Task<bool> ResetTrafficAsync(ISshService ssh, string email) { try { await ssh.ExecuteCommandAsync($"/usr/local/bin/xray api statsquery --server=127.0.0.1:10085 --pattern \"user>>>{email}>>>traffic\" --reset"); return true; } catch { return false; } }
    public async Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string email, bool active)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
        if (user == null) return (false, "Нет в БД");
        user.IsActive = active; await _dbContext.SaveChangesAsync();
        var root = JsonNode.Parse(await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json"));
        await RebuildInboundsAsync(root, serverIp, ssh);
        return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<bool> UpdateUserLimitsAsync(string serverIp, string email, long limit, DateTime? expiry)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
        if (user == null) return false;
        user.TrafficLimit = limit; user.ExpiryDate = expiry; await _dbContext.SaveChangesAsync(); return true;
    }

    public async Task SaveTrafficToDbAsync(string ip, IEnumerable<VpnClient> clients)
    {
        var users = await _dbContext.Clients.Where(c => c.ServerIp == ip).ToListAsync();
        foreach (var c in clients) { var u = users.FirstOrDefault(x => x.Email == c.Email); if (u != null) u.TrafficUsed = c.TrafficUsed; }
        await _dbContext.SaveChangesAsync();
    }

    public async Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> dbUsers)
    {
        try {
            var root = JsonNode.Parse(await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json"));
            string ip = dbUsers.FirstOrDefault()?.ServerIp ?? "";
            if (!string.IsNullOrEmpty(ip)) await RebuildInboundsAsync(root, ip, ssh);
            return (await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))).IsSuccess;
        } catch { return false; }
    }
}
