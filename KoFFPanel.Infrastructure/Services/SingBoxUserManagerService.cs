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
        // === MODERN 2026: SOURCE OF TRUTH ===
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();

        if (dbUsers.Count == 0)
        {
            var admin = new VpnClient { Email = "ADMIN", Uuid = Guid.NewGuid().ToString(), ServerIp = serverIp, Protocol = "VLESS", IsActive = true, IsP2PBlocked = true, IsVlessEnabled = true, IsTrustTunnelEnabled = true, IsHysteria2Enabled = true };
            _dbContext.Clients.Add(admin); await _dbContext.SaveChangesAsync();
            dbUsers.Add(admin);
        }

        if (ssh.IsConnected)
        {
            string raw = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json 2>/dev/null");
            if (!string.IsNullOrWhiteSpace(raw) && raw.Contains("{"))
            {
                try {
                    var root = JsonNode.Parse(raw);
                    if (root != null) {
                        await RebuildInboundsAsync(root, serverIp);
                        await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                } catch { }
            }
        }
        return await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
    }

    public async Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string name, long limit, DateTime? expiry, bool p2p = true, bool isVless = true, bool isHy2 = true, bool isTt = true)
    {
        try { SshGuard.ThrowIfInvalid(name, null); } catch (Exception ex) { return (false, ex.Message, ""); }
        if (await _dbContext.Clients.AnyAsync(c => c.Email == name && c.ServerIp == serverIp)) return (false, "Уже есть!", "");
        
        var newUser = new VpnClient 
        { 
            Email = name, 
            Uuid = Guid.NewGuid().ToString(), 
            ServerIp = serverIp, 
            TrafficLimit = limit, 
            ExpiryDate = expiry, 
            IsP2PBlocked = p2p, 
            IsVlessEnabled = isVless,
            IsHysteria2Enabled = isHy2,
            IsTrustTunnelEnabled = isTt,
            IsActive = true 
        };
        _dbContext.Clients.Add(newUser); await _dbContext.SaveChangesAsync();
        
        var rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json");
        var root = JsonNode.Parse(rawJson);
        if (root != null)
        {
            await RebuildInboundsAsync(root, serverIp); await ApplyP2PRulesAsync(root, serverIp);
            var res = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return (res.IsSuccess, res.Message, (await _dbContext.Clients.FirstOrDefaultAsync(u => u.Uuid == newUser.Uuid))?.VlessLink ?? "");
        }
        return (false, "Ошибка чтения конфига сервера", "");
    }

    public async Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string name)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
        if (await _dbContext.Clients.CountAsync(c => c.ServerIp == serverIp) <= 1) return (false, "Последний!");
        if (user != null) { _dbContext.Clients.Remove(user); await _dbContext.SaveChangesAsync(); }
        
        var rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json 2>/dev/null");
        if (string.IsNullOrWhiteSpace(rawJson)) return (true, "Удален.");
        var root = JsonNode.Parse(rawJson);
        if (root == null) return (true, "Удален.");

        await RebuildInboundsAsync(root, serverIp); await ApplyP2PRulesAsync(root, serverIp);
        return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string name, bool active)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
        if (user == null) return (false, "Нет в БД");
        user.IsActive = active; await _dbContext.SaveChangesAsync();

        var rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json");
        var root = JsonNode.Parse(rawJson);
        if (root == null) return (false, "Ошибка конфига");

        await RebuildInboundsAsync(root, serverIp); await ApplyP2PRulesAsync(root, serverIp);
        return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<bool> UpdateUserLimitsAsync(ISshService ssh, string serverIp, string name, long limit, DateTime? expiry, bool p2p = true, bool isVless = true, bool isHy2 = true, bool isTt = true)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
        if (user == null) return false;
        user.TrafficLimit = limit; user.ExpiryDate = expiry; user.IsP2PBlocked = p2p;
        user.IsVlessEnabled = isVless; user.IsHysteria2Enabled = isHy2; user.IsTrustTunnelEnabled = isTt;
        await _dbContext.SaveChangesAsync();

        var rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json");
        var root = JsonNode.Parse(rawJson);
        if (root == null) return false;

        await RebuildInboundsAsync(root, serverIp); await ApplyP2PRulesAsync(root, serverIp);
        return (await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))).IsSuccess;
    }

    public async Task SaveTrafficToDbAsync(string ip, IEnumerable<VpnClient> clients)
    {
        var users = await _dbContext.Clients.Where(c => c.ServerIp == ip).ToListAsync();
        foreach (var c in clients) { var u = users.FirstOrDefault(x => x.Email == c.Email); if (u != null) u.TrafficUsed = c.TrafficUsed; }
        await _dbContext.SaveChangesAsync();
    }

    public async Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> clients)
    {
        try {
            string serverIp = clients.FirstOrDefault()?.ServerIp ?? "";
            if (string.IsNullOrEmpty(serverIp)) return false;

            // Сначала актуализируем БД из переданного списка
            var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
            foreach (var client in clients)
            {
                var dbUser = dbUsers.FirstOrDefault(u => u.Email == client.Email);
                if (dbUser != null)
                {
                    dbUser.IsVlessEnabled = client.IsVlessEnabled;
                    dbUser.IsHysteria2Enabled = client.IsHysteria2Enabled;
                    dbUser.IsTrustTunnelEnabled = client.IsTrustTunnelEnabled;
                    dbUser.IsP2PBlocked = client.IsP2PBlocked;
                    dbUser.IsActive = client.IsActive;
                    dbUser.TrafficLimit = client.TrafficLimit;
                    dbUser.ExpiryDate = client.ExpiryDate;
                    dbUser.Note = client.Note;
                }
            }
            await _dbContext.SaveChangesAsync();

            if (!ssh.IsConnected) return true;

            var rawJson = await ssh.ExecuteCommandAsync("cat /etc/sing-box/config.json");
            var root = JsonNode.Parse(rawJson);
            if (root == null) return false;

            await RebuildInboundsAsync(root, serverIp);
            return (await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))).IsSuccess;
        } catch { return false; }
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
