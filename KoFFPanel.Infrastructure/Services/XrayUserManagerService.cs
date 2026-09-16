using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public partial class XrayUserManagerService : IXrayUserManagerService
{
    private readonly IAppLogger _logger;
    private readonly AppDbContext _dbContext;
    private readonly IProfileRepository _profileRepository; // ИСПРАВЛЕНИЕ: Добавлен репозиторий

    public XrayUserManagerService(IAppLogger logger, AppDbContext dbContext, IProfileRepository profileRepository)
    {
        _logger = logger;
        _dbContext = dbContext;
        _profileRepository = profileRepository;
    }

    public async Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();

        if (dbUsers.Count == 0)
        {
            var admin = new VpnClient { Email = "ADMIN", Uuid = Guid.NewGuid().ToString(), ServerIp = serverIp, Protocol = "VLESS", IsActive = true, IsP2PBlocked = true, IsVlessEnabled = true };
            _dbContext.Clients.Add(admin);
            await _dbContext.SaveChangesAsync();
            dbUsers.Add(admin);
        }

        return dbUsers;
    }

    public async Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string email, long limit, DateTime? expiry, bool isP2PBlocked = true, bool isVless = true, bool isHy2 = false, bool isTrojan = false)
    {
        try { SshGuard.ThrowIfInvalid(email, null); } catch (Exception ex) { return (false, ex.Message, ""); }
        if (await _dbContext.Clients.AnyAsync(c => c.Email == email && c.ServerIp == serverIp)) return (false, "Уже есть!", "");

        var user = new VpnClient
        {
            Email = email,
            Uuid = Guid.NewGuid().ToString(),
            ServerIp = serverIp,
            Protocol = "VLESS",
            TrafficLimit = limit,
            ExpiryDate = expiry,
            IsActive = true,
            IsP2PBlocked = isP2PBlocked,
            IsVlessEnabled = isVless,
            IsHysteria2Enabled = isHy2,
            IsTrojanEnabled = isTrojan
        };

        _dbContext.Clients.Add(user); await _dbContext.SaveChangesAsync();

        if (ssh.IsConnected)
        {
            var rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null");
            if (!string.IsNullOrWhiteSpace(rawJson) && rawJson.Contains("{"))
            {
                var root = JsonNode.Parse(rawJson);
                if (root != null)
                {
                    await RebuildInboundsAsync(root, serverIp, ssh);
                    var res = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    if (!res.IsSuccess) return (false, res.Message, "");
                }
            }
        }
        var savedUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
        return (true, "Пользователь добавлен!", savedUser?.VlessLink ?? "");
    }

    public async Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string email)
    {
        if (email.Equals("Админ", StringComparison.OrdinalIgnoreCase)) return (false, "Нельзя удалить Админа!");
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
        if (await _dbContext.Clients.CountAsync(c => c.ServerIp == serverIp) <= 1) return (false, "Последний!");
        if (user != null) { _dbContext.Clients.Remove(user); await _dbContext.SaveChangesAsync(); }

        var rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null");
        if (string.IsNullOrWhiteSpace(rawJson)) return (true, "Удален.");
        var root = JsonNode.Parse(rawJson);
        if (root == null) return (true, "Удален.");

        await RebuildInboundsAsync(root, serverIp, ssh);
        return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh)
    {
        var stats = new Dictionary<string, long>();
        try
        {
            var raw = await ssh.ExecuteCommandAsync("/usr/local/bin/xray api statsquery --server=127.0.0.1:10085");
            var root = JsonNode.Parse(raw);
            if (root == null) return stats;

            foreach (var item in root["stat"]?.AsArray() ?? new JsonArray())
            {
                var parts = item?["name"]?.ToString().Split(">>>");
                if (parts?.Length == 4 && parts[0] == "user")
                {
                    string email = parts[1]; long val = long.Parse(item?["value"]?.ToString() ?? "0");
                    if (stats.ContainsKey(email)) stats[email] += val; else stats[email] = val;
                }
            }
        }
        catch { }
        return stats;
    }

    public async Task<bool> ResetTrafficAsync(ISshService ssh, string email) { try { await ssh.ExecuteCommandAsync($"/usr/local/bin/xray api statsquery --server=127.0.0.1:10085 --pattern \"user>>>{email}>>>traffic\" --reset"); return true; } catch { return false; } }

    public async Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string email, bool active)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
        if (user == null) return (false, "Нет в БД");
        user.IsActive = active; await _dbContext.SaveChangesAsync();

        var rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json");
        var root = JsonNode.Parse(rawJson);
        if (root == null) return (false, "Ошибка конфига");

        await RebuildInboundsAsync(root, serverIp, ssh);
        return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<bool> UpdateUserLimitsAsync(ISshService ssh, string serverIp, string email, long limit, DateTime? expiry, string note, bool isP2PBlocked = true, bool isVless = true, bool isHy2 = false, bool isTrojan = false)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
        if (user == null) return false;

        user.TrafficLimit = limit; user.ExpiryDate = expiry; user.Note = note; user.IsP2PBlocked = isP2PBlocked;
        user.IsVlessEnabled = isVless; user.IsHysteria2Enabled = isHy2;
        user.IsTrojanEnabled = isTrojan;
        await _dbContext.SaveChangesAsync();

        if (ssh.IsConnected)
        {
            var rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json");
            var root = JsonNode.Parse(rawJson);
            if (root != null)
            {
                await RebuildInboundsAsync(root, serverIp, ssh);
                var res = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                return res.IsSuccess;
            }
        }
        return true;
    }

    public async Task SaveTrafficToDbAsync(string ip, IEnumerable<VpnClient> clients)
    {
        var users = await _dbContext.Clients.Where(c => c.ServerIp == ip).ToListAsync();
        foreach (var c in clients) { var u = users.FirstOrDefault(x => x.Email == c.Email); if (u != null) u.TrafficUsed = c.TrafficUsed; }
        await _dbContext.SaveChangesAsync();
    }

    public async Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> clients)
    {
        try
        {
            string serverIp = clients.FirstOrDefault()?.ServerIp ?? "";
            if (string.IsNullOrEmpty(serverIp)) return false;

            var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
            foreach (var client in clients)
            {
                var dbUser = dbUsers.FirstOrDefault(u => u.Email == client.Email);
                if (dbUser != null)
                {
                    dbUser.IsVlessEnabled = client.IsVlessEnabled;
                    dbUser.IsHysteria2Enabled = client.IsHysteria2Enabled;
                    dbUser.IsTrojanEnabled = client.IsTrojanEnabled;
                    dbUser.IsP2PBlocked = client.IsP2PBlocked;
                    dbUser.IsActive = client.IsActive;
                    dbUser.TrafficLimit = client.TrafficLimit;
                    dbUser.ExpiryDate = client.ExpiryDate;
                    dbUser.Note = client.Note;
                }
            }
            await _dbContext.SaveChangesAsync();

            if (!ssh.IsConnected) return true;

            var rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json");
            var root = JsonNode.Parse(rawJson);
            if (root == null) return false;

            await RebuildInboundsAsync(root, serverIp, ssh);
            return (await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))).IsSuccess;
        }
        catch { return false; }
    }
}