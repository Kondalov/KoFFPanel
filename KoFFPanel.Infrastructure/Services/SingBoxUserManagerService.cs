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

public partial class SingBoxUserManagerService : ISingBoxUserManagerService
{
    private readonly IAppLogger _logger;
    private readonly AppDbContext _dbContext;
    private readonly IProfileRepository _profileRepository;

    public SingBoxUserManagerService(IAppLogger logger, AppDbContext dbContext, IProfileRepository profileRepository)
    {
        _logger = logger; _dbContext = dbContext; _profileRepository = profileRepository;
    }

    public async Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        var globalUsers = await _dbContext.Clients.Where(c => c.ServerIp != serverIp).ToListAsync();
        bool isMigrated = false;

        foreach (var gu in globalUsers.GroupBy(u => u.Email).Select(g => g.First()))
        {
            if (!dbUsers.Any(u => u.Email == gu.Email))
            {
                var newUser = new VpnClient 
                { 
                    Email = gu.Email, 
                    Uuid = string.IsNullOrEmpty(gu.Uuid) ? Guid.NewGuid().ToString() : gu.Uuid, 
                    ServerIp = serverIp, 
                    Protocol = gu.Protocol, 
                    TrafficLimit = gu.TrafficLimit, 
                    ExpiryDate = gu.ExpiryDate, 
                    IsActive = gu.IsActive, 
                    IsP2PBlocked = gu.IsP2PBlocked, 
                    IsVlessEnabled = gu.IsVlessEnabled,
                    IsHysteria2Enabled = gu.IsHysteria2Enabled,
                    IsTrustTunnelEnabled = gu.IsTrustTunnelEnabled
                };
                _dbContext.Clients.Add(newUser);
                dbUsers.Add(newUser);
                isMigrated = true;
            }
        }

        // Create Admin if NO users exist globally and locally
        if (dbUsers.Count == 0 && globalUsers.Count == 0)
        {
            var admin = new VpnClient { Email = "ADMIN", Uuid = Guid.NewGuid().ToString(), ServerIp = serverIp, Protocol = "VLESS", IsActive = true, IsP2PBlocked = true, IsVlessEnabled = true, IsTrustTunnelEnabled = true, IsHysteria2Enabled = true };
            _dbContext.Clients.Add(admin);
            dbUsers.Add(admin);
            isMigrated = true;
        }

        // ИСПРАВЛЕНИЕ: Удаление дублей админа (ADMIN vs Админ)
        var oldAdmin = dbUsers.FirstOrDefault(u => u.Email == "Админ");
        var standardAdmin = dbUsers.FirstOrDefault(u => u.Email == "ADMIN");

        if (oldAdmin != null)
        {
            if (standardAdmin == null)
            {
                oldAdmin.Email = "ADMIN";
                isMigrated = true;
            }
            else
            {
                _dbContext.Clients.Remove(oldAdmin);
                dbUsers.Remove(oldAdmin);
                isMigrated = true;
            }
        }

        if (isMigrated) await _dbContext.SaveChangesAsync();

        if (!ssh.IsConnected) return dbUsers;
        string raw = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json 2>/dev/null");
        if (string.IsNullOrWhiteSpace(raw) || !raw.Contains("{")) return dbUsers;
        try {
            var root = JsonNode.Parse(raw);
            var vless = root?["inbounds"]?.AsArray()?.FirstOrDefault(i => i?["type"]?.ToString() == "vless");
            if (vless != null) {
                bool changedFromConfig = false;
                foreach (var c in vless["users"]?.AsArray() ?? new JsonArray()) {
                    string uuid = c?["uuid"]?.ToString() ?? "";
                    if (!dbUsers.Any(u => u.Uuid == uuid)) {
                        _dbContext.Clients.Add(new VpnClient { Email = c?["name"]?.ToString() ?? "Unknown", Uuid = uuid, ServerIp = serverIp, Protocol = "VLESS", IsActive = true, IsP2PBlocked = true, IsVlessEnabled = true });
                        changedFromConfig = true;
                    }
                }
                if (changedFromConfig) await _dbContext.SaveChangesAsync();
            }
            await RebuildInboundsAsync(root, serverIp);
            return await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        } catch { return dbUsers; }
    }

    public async Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string name, long limit, DateTime? expiry, bool p2p = true)
    {
        try { SshGuard.ThrowIfInvalid(name, null); } catch (Exception ex) { return (false, ex.Message, ""); }
        if (await _dbContext.Clients.AnyAsync(c => c.Email == name && c.ServerIp == serverIp)) return (false, "Уже есть!", "");
        var newUser = new VpnClient { Email = name, Uuid = Guid.NewGuid().ToString(), ServerIp = serverIp, TrafficLimit = limit, ExpiryDate = expiry, IsP2PBlocked = p2p, IsVlessEnabled = true, IsActive = true };
        _dbContext.Clients.Add(newUser); await _dbContext.SaveChangesAsync();
        var root = JsonNode.Parse(await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json"));
        await RebuildInboundsAsync(root, serverIp); await ApplyP2PRulesAsync(root, serverIp);
        var res = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return (res.IsSuccess, res.Message, (await _dbContext.Clients.FirstOrDefaultAsync(u => u.Uuid == newUser.Uuid))?.VlessLink ?? "");
    }

    public async Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string name)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
        if (await _dbContext.Clients.CountAsync(c => c.ServerIp == serverIp) <= 1) return (false, "Последний!");
        if (user != null) { _dbContext.Clients.Remove(user); await _dbContext.SaveChangesAsync(); }
        var root = JsonNode.Parse(await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json 2>/dev/null"));
        if (root == null) return (true, "Удален.");
        await RebuildInboundsAsync(root, serverIp); await ApplyP2PRulesAsync(root, serverIp);
        return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string name, bool active)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
        if (user == null) return (false, "Нет в БД");
        user.IsActive = active; await _dbContext.SaveChangesAsync();
        var root = JsonNode.Parse(await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json"));
        await RebuildInboundsAsync(root, serverIp); await ApplyP2PRulesAsync(root, serverIp);
        return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<bool> UpdateUserLimitsAsync(ISshService ssh, string serverIp, string name, long limit, DateTime? expiry, bool p2p = true)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
        if (user == null) return false;
        user.TrafficLimit = limit; user.ExpiryDate = expiry; user.IsP2PBlocked = p2p;
        await _dbContext.SaveChangesAsync();
        var root = JsonNode.Parse(await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json"));
        await RebuildInboundsAsync(root, serverIp); await ApplyP2PRulesAsync(root, serverIp);
        return (await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))).IsSuccess;
    }

    public async Task SaveTrafficToDbAsync(string ip, IEnumerable<VpnClient> clients)
    {
        var users = await _dbContext.Clients.Where(c => c.ServerIp == ip).ToListAsync();
        foreach (var c in clients) { var u = users.FirstOrDefault(x => x.Email == c.Email); if (u != null) u.TrafficUsed = c.TrafficUsed; }
        await _dbContext.SaveChangesAsync();
    }

    public async Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> dbUsers)
    {
        var root = JsonNode.Parse(await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json"));
        string ip = dbUsers.FirstOrDefault()?.ServerIp ?? "";
        if (!string.IsNullOrEmpty(ip)) { await RebuildInboundsAsync(root, ip); await ApplyP2PRulesAsync(root, ip); }
        return (await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))).IsSuccess;
    }

    public async Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh) => new();
    public async Task<bool> ResetTrafficAsync(ISshService ssh, string name) => true;
}
