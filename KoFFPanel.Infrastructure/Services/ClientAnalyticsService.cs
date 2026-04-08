using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class ClientAnalyticsService : IClientAnalyticsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAppLogger _logger;

    public ClientAnalyticsService(IServiceScopeFactory scopeFactory, IAppLogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task SaveBatchAsync(string serverIp, Dictionary<string, long> trafficDeltas, List<(string Email, string Ip, string Country)> connections, List<(string Email, string ViolationType)> violations)
    {
        if (!trafficDeltas.Any() && !connections.Any() && !violations.Any()) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var today = DateTime.Today;

        try
        {
            foreach (var t in trafficDeltas)
            {
                var log = await db.TrafficLogs.FirstOrDefaultAsync(x => x.ServerIp == serverIp && x.Email == t.Key && x.Date == today);
                if (log != null) log.BytesUsed += t.Value;
                else db.TrafficLogs.Add(new ClientTrafficLog { ServerIp = serverIp, Email = t.Key, Date = today, BytesUsed = t.Value });
            }

            foreach (var c in connections)
            {
                var log = await db.ConnectionLogs.FirstOrDefaultAsync(x => x.ServerIp == serverIp && x.Email == c.Email && x.IpAddress == c.Ip);
                if (log != null) log.LastSeen = DateTime.Now;
                else db.ConnectionLogs.Add(new ClientConnectionLog { ServerIp = serverIp, Email = c.Email, IpAddress = c.Ip, Country = c.Country, FirstSeen = DateTime.Now, LastSeen = DateTime.Now });
            }

            // Запись нарушений. Чтобы не спамить базу, пишем 1 нарушение в час для юзера.
            var oneHourAgo = DateTime.Now.AddHours(-1);
            foreach (var v in violations)
            {
                var recentViol = await db.ViolationLogs.FirstOrDefaultAsync(x => x.ServerIp == serverIp && x.Email == v.Email && x.ViolationType == v.ViolationType && x.Date > oneHourAgo);
                if (recentViol == null)
                {
                    db.ViolationLogs.Add(new ClientViolationLog { ServerIp = serverIp, Email = v.Email, Date = DateTime.Now, ViolationType = v.ViolationType });
                }
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.Log("ANALYTICS", $"Ошибка пакетного сохранения логов: {ex.Message}");
        }
    }

    public async Task<List<ClientTrafficLog>> GetTrafficLogsAsync(string serverIp, string email, int days = 30)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var limitDate = DateTime.Today.AddDays(-days);
        return await db.TrafficLogs.Where(x => x.ServerIp == serverIp && x.Email == email && x.Date >= limitDate).OrderByDescending(x => x.Date).ToListAsync();
    }

    public async Task<List<ClientConnectionLog>> GetConnectionLogsAsync(string serverIp, string email)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ConnectionLogs.Where(x => x.ServerIp == serverIp && x.Email == email).OrderByDescending(x => x.LastSeen).ToListAsync();
    }

    public async Task<List<ClientViolationLog>> GetViolationLogsAsync(string serverIp, string email)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ViolationLogs.Where(x => x.ServerIp == serverIp && x.Email == email).OrderByDescending(x => x.Date).ToListAsync();
    }

    public async Task CleanupOldLogsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var oldTraffic = DateTime.Today.AddDays(-365);
        var oldConnections = DateTime.Today.AddDays(-30);
        var oldViolations = DateTime.Today.AddDays(-90); // Нарушения храним 3 месяца

        db.TrafficLogs.RemoveRange(db.TrafficLogs.Where(x => x.Date < oldTraffic));
        db.ConnectionLogs.RemoveRange(db.ConnectionLogs.Where(x => x.LastSeen < oldConnections));
        db.ViolationLogs.RemoveRange(db.ViolationLogs.Where(x => x.Date < oldViolations));

        await db.SaveChangesAsync();
    }
}