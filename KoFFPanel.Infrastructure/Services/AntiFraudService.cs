using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class AntiFraudService : IAntiFraudService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAppLogger _logger;

    // В памяти храним последние ASN и Гео для каждого пользователя для быстрого детектирования прыжков
    private static readonly Dictionary<string, HashSet<string>> _dailyAsns = new();
    private static readonly Dictionary<string, string> _lastCountryCode = new();
    private static readonly Dictionary<string, DateTime> _lastCountryTime = new();

    public AntiFraudService(IServiceScopeFactory scopeFactory, IAppLogger logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<(bool IsFraud, string Reason)> EvaluateClientAsync(string serverIp, VpnClient client, string currentIp, long trafficDelta, CancellationToken token = default)
    {
        if (!client.IsAntiFraudEnabled) return (false, "");
        string email = client.Email ?? "Unknown";

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var today = DateTime.Today;
        var log = await db.BehaviorLogs.FirstOrDefaultAsync(x => x.ServerIp == serverIp && x.Email == email && x.Date == today, token);

        if (log == null)
        {
            log = new ClientBehaviorLog { ServerIp = serverIp, Email = email, Date = today, RiskScore = 0 };
            db.BehaviorLogs.Add(log);
            ClearInMemCachesForNewDay(email);
        }

        UpdateMetrics(log, client, currentIp, trafficDelta);
        CalculateRiskScore(log);

        await db.SaveChangesAsync(token);

        if (log.RiskScore >= 100)
        {
            return (true, $"ФРОД 100%: Сессий={log.MaxConcurrentSessions}, ASN={log.UniqueAsnCount}, GeoJumps={log.GeoJumpsCount}");
        }

        return (false, "");
    }

    private void UpdateMetrics(ClientBehaviorLog log, VpnClient client, string currentIp, long trafficDelta)
    {
        string email = log.Email;

        // 1. Макс сессии
        if (client.ActiveConnections > log.MaxConcurrentSessions)
            log.MaxConcurrentSessions = client.ActiveConnections;

        // 2. ASN анализ (Mock: в реале тут MaxMind ASN Reader, пока используем эвристику подсетей)
        string subnetAsn = GetMockAsnFromIp(currentIp);
        if (!_dailyAsns.ContainsKey(email)) _dailyAsns[email] = new HashSet<string>();
        if (!string.IsNullOrEmpty(subnetAsn) && _dailyAsns[email].Add(subnetAsn))
        {
            log.UniqueAsnCount = _dailyAsns[email].Count;
        }

        // 3. Geo Jump (Impossible Travel)
        string curCode = client.Country ?? "";
        if (curCode.Length >= 2)
        {
            curCode = curCode.Substring(curCode.Length - 2);
            if (_lastCountryCode.TryGetValue(email, out string? lastCode) && lastCode != curCode)
            {
                if (_lastCountryTime.TryGetValue(email, out DateTime lastTime) && (DateTime.Now - lastTime).TotalHours < 2)
                {
                    log.GeoJumpsCount++;
                }
            }
            _lastCountryCode[email] = curCode;
            _lastCountryTime[email] = DateTime.Now;
        }

        // 4. Трафик спайки
        if (trafficDelta > 1073741824L) // > 1 GB за цикл
            log.BytesUsedSpike += trafficDelta;
    }

    private void CalculateRiskScore(ClientBehaviorLog log)
    {
        int score = 0;

        // Умная математика весов
        if (log.MaxConcurrentSessions > 2) score += (log.MaxConcurrentSessions - 2) * 40;
        if (log.UniqueAsnCount > 3) score += (log.UniqueAsnCount - 3) * 20; // 3 легитимных провайдера в день - норма
        if (log.GeoJumpsCount > 0) score += log.GeoJumpsCount * 80;
        if (log.BytesUsedSpike > 0) score += 30;

        log.RiskScore = score > 100 ? 100 : score;
    }

    private string GetMockAsnFromIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "";
        var parts = ip.Split('.');
        return parts.Length == 4 ? $"AS_{parts[0]}.{parts[1]}" : "AS_IPv6";
    }

    public async Task<List<ClientBehaviorLog>> GetMonthlyBehaviorAsync(string serverIp, string email, CancellationToken token = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var limitDate = DateTime.Today.AddDays(-30);
        var rawLogs = await db.BehaviorLogs.AsNoTracking().Where(x => x.ServerIp == serverIp && x.Email == email).ToListAsync(token);

        return rawLogs.Where(x => x.Date >= limitDate).OrderByDescending(x => x.Date).ToList();
    }

    public async Task ExecuteMonthlyRetentionPolicyAsync(CancellationToken token = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var thresholdDate = DateTime.Today.AddDays(-30);
        var oldLogs = await db.BehaviorLogs.ToListAsync(token);
        var toRemove = oldLogs.Where(x => x.Date < thresholdDate).ToList();

        if (toRemove.Any())
        {
            db.BehaviorLogs.RemoveRange(toRemove);
            await db.SaveChangesAsync(token);
            _logger.Log("ANTIFRAUD-CLEANUP", $"Удалено {toRemove.Count} устаревших скоринг-записей.");
        }
    }

    private void ClearInMemCachesForNewDay(string email)
    {
        _dailyAsns.Remove(email);
    }
}