using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KoFFPanel.Infrastructure.Services.ProtocolBuilders;

namespace KoFFPanel.Infrastructure.Services;

public class TrustTunnelUserManagerService : ITrustTunnelUserManagerService
{
    private readonly IAppLogger _logger;
    private readonly AppDbContext _dbContext;
    private readonly IProfileRepository _profileRepository;

    public TrustTunnelUserManagerService(IAppLogger logger, AppDbContext dbContext, IProfileRepository profileRepository)
    {
        _logger = logger;
        _dbContext = dbContext;
        _profileRepository = profileRepository;
    }

    private async Task RebuildCredentialsAsync(ISshService ssh, string serverIp)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        var sb = new StringBuilder();

        foreach (var u in dbUsers)
        {
            if (u.IsActive && u.IsTrustTunnelEnabled)
            {
                sb.AppendLine("[[client]]");
                sb.AppendLine($"username = \"{u.Email}\"");
                sb.AppendLine($"password = \"{u.Uuid}\"");
                sb.AppendLine();
            }
        }

        string base64Creds = Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        await ssh.ExecuteCommandAsync($"echo '{base64Creds}' | base64 -d | sudo tee /opt/trusttunnel2/credentials.toml >/dev/null");

        var profile = _profileRepository.LoadProfiles().FirstOrDefault(p => p.IpAddress == serverIp);
        var ttInbound = profile?.Inbounds.FirstOrDefault(i => i.Protocol.ToLower() == "trusttunnel");
        if (ttInbound != null)
        {
            var builder = new TrustTunnelBuilder();
            foreach (var u in dbUsers)
            {
                u.TrustTunnelLink = builder.GenerateClientLink(ttInbound, serverIp, u.Uuid, u.Email);
            }
        }

        await _dbContext.SaveChangesAsync();
    }

    public async Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp)
    {
        if (ssh.IsConnected) await RebuildCredentialsAsync(ssh, serverIp);
        return await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
    }

    public async Task<(bool IsSuccess, string Message, string TrustTunnelLink)> AddUserAsync(ISshService ssh, string serverIp, string name, long trafficLimitBytes, DateTime? expiryDate, bool isP2PBlocked = true, bool isVless = false, bool isHy2 = false, bool isTt = true)
    {
        try
        {
            var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.Email == name && c.ServerIp == serverIp);
            if (dbUser != null) return (false, $"Пользователь {name} уже существует!", "");

            var newUser = new VpnClient
            {
                Email = name,
                Uuid = Guid.NewGuid().ToString(),
                ServerIp = serverIp,
                TrafficLimit = trafficLimitBytes,
                ExpiryDate = expiryDate,
                IsP2PBlocked = isP2PBlocked,
                IsVlessEnabled = isVless,
                IsHysteria2Enabled = isHy2,
                IsTrustTunnelEnabled = isTt
            };

            _dbContext.Clients.Add(newUser);
            await _dbContext.SaveChangesAsync();

            await RebuildCredentialsAsync(ssh, serverIp);
            await ssh.ExecuteCommandAsync("systemctl restart trusttunnel");

            var savedUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.Email == name && c.ServerIp == serverIp);
            return (true, "Пользователь добавлен!", savedUser?.TrustTunnelLink ?? "");
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

            await RebuildCredentialsAsync(ssh, serverIp);
            await ssh.ExecuteCommandAsync("systemctl restart trusttunnel");

            return (true, $"Пользователь {name} удален.");
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

            await RebuildCredentialsAsync(ssh, serverIp);
            await ssh.ExecuteCommandAsync("systemctl restart trusttunnel");

            return (true, "Статус обновлен!");
        }
        catch (Exception ex) { return (false, $"Ошибка статуса: {ex.Message}"); }
    }

    public async Task<bool> UpdateUserLimitsAsync(ISshService ssh, string serverIp, string name, long newLimitBytes, DateTime? newExpiryDate, bool isP2PBlocked = true, bool isVless = false, bool isHy2 = false, bool isTt = true)
    {
        var dbUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == name);
        if (dbUser == null) return false;

        dbUser.TrafficLimit = newLimitBytes; 
        dbUser.ExpiryDate = newExpiryDate; 
        dbUser.IsP2PBlocked = isP2PBlocked;
        dbUser.IsVlessEnabled = isVless;
        dbUser.IsHysteria2Enabled = isHy2;
        dbUser.IsTrustTunnelEnabled = isTt;

        try
        {
            await _dbContext.SaveChangesAsync();
            await RebuildCredentialsAsync(ssh, serverIp);
            await ssh.ExecuteCommandAsync("systemctl restart trusttunnel");
            return true;
        }
        catch (Exception ex) { _logger.Log("TT-USER-MGR", $"Ошибка: {ex.Message}"); return false; }
    }

    public async Task SaveTrafficToDbAsync(string serverIp, IEnumerable<VpnClient> clients)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        foreach (var client in clients) { var dbUser = dbUsers.FirstOrDefault(u => u.Email == client.Email); if (dbUser != null) dbUser.TrafficUsed = client.TrafficUsed; }
        await _dbContext.SaveChangesAsync();
    }

    public async Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> clients)
    {
        try
        {
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

            if (ssh.IsConnected)
            {
                await RebuildCredentialsAsync(ssh, serverIp);
                await ssh.ExecuteCommandAsync("systemctl restart trusttunnel");
            }
            _logger.Log("USER-SYNC", "Синхронизация TrustTunnel завершена");
            return true;
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