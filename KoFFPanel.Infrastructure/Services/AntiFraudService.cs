using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class AntiFraudService : IAntiFraudService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IAppLogger _logger;

    private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _dailyAsns = new();
    private static readonly ConcurrentDictionary<string, string> _lastCountryCode = new();
    private static readonly ConcurrentDictionary<string, DateTime> _lastCountryTime = new();

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

        if (client.ActiveConnections > log.MaxConcurrentSessions)
            log.MaxConcurrentSessions = client.ActiveConnections;

        string asn = ResolveAsnFromIp(currentIp);
        if (!string.IsNullOrEmpty(asn))
        {
            var userAsns = _dailyAsns.GetOrAdd(email, _ => new ConcurrentDictionary<string, byte>());
            userAsns.TryAdd(asn, 0);
            log.UniqueAsnCount = userAsns.Count;
        }

        // Вызов защищенного метода проверки геолокации
        UpdateGeoMetrics(log, email, client.Country);

        if (trafficDelta > 524_288_000L && trafficDelta < 50_000_000_000L)
            log.BytesUsedSpike += trafficDelta;
    }

    private void UpdateGeoMetrics(ClientBehaviorLog log, string email, string? rawCountry)
    {
        string curCode = GetValidCountryCode(rawCountry);

        if (string.IsNullOrEmpty(curCode)) return;

        if (!_lastCountryCode.TryGetValue(email, out string? lastCode) || string.IsNullOrEmpty(lastCode))
        {
            _lastCountryCode[email] = curCode;
            _lastCountryTime[email] = DateTime.Now;
            return;
        }

        if (lastCode != curCode)
        {
            if (_lastCountryTime.TryGetValue(email, out DateTime lastTime) && (DateTime.Now - lastTime).TotalHours < 2)
            {
                log.GeoJumpsCount++;
                _logger.Log("ANTIFRAUD", $"Зафиксирован GeoJump для {email}: {lastCode} -> {curCode}");
            }
            _lastCountryCode[email] = curCode;
        }

        _lastCountryTime[email] = DateTime.Now;
    }

    private string GetValidCountryCode(string? rawCountry)
    {
        if (string.IsNullOrWhiteSpace(rawCountry)) return "";

        string code = rawCountry.Trim();
        if (code.Length >= 2) code = code.Substring(code.Length - 2).ToUpperInvariant();

        if (code == "??" || code == "A1" || code == "O1" || !code.All(char.IsLetter))
            return "";

        return code;
    }

    public void CalculateRiskScore(ClientBehaviorLog log)
    {
        int score = 0;

        if (log.MaxConcurrentSessions > 8) score += (log.MaxConcurrentSessions - 8) * 10;
        if (log.UniqueAsnCount > 3) score += (log.UniqueAsnCount - 3) * 20;
        if (log.GeoJumpsCount > 0) score += log.GeoJumpsCount * 80;
        if (log.BytesUsedSpike > 0) score += 30;

        log.RiskScore = score > 100 ? 100 : score;
    }

    private string ResolveAsnFromIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return "";
        string cleanIp = ip.Trim().Trim('[', ']');
        if (cleanIp.Contains(':') && cleanIp.IndexOf(':') == cleanIp.LastIndexOf(':'))
            cleanIp = cleanIp.Split(':')[0];

        if (!IPAddress.TryParse(cleanIp, out var parsedIp)) return "";

        // Проверка наличия локальной базы ASN
        string asnDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeoLite2-ASN.mmdb");
        if (File.Exists(asnDbPath))
        {
            try
            {
                using var reader = new MaxMind.GeoIP2.DatabaseReader(asnDbPath);
                if (reader.TryAsn(parsedIp, out var asnResponse) && asnResponse?.AutonomousSystemNumber != null)
                {
                    return $"AS{asnResponse.AutonomousSystemNumber}";
                }
            }
            catch { }
        }

        // Защищенный fallback: группируем по /24 для IPv4 (не /16) или по первому блоку для IPv6, чтобы не спамить ложными ASN
        if (parsedIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = parsedIp.GetAddressBytes();
            return $"NET_{bytes[0]}.{bytes[1]}.{bytes[2]}";
        }
        return "NET_IPv6";
    }

    public async Task<List<ClientBehaviorLog>> GetMonthlyBehaviorAsync(string serverIp, string email, CancellationToken token = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var limitDate = DateTime.Today.AddDays(-30);

        return await db.BehaviorLogs
            .AsNoTracking()
            .Where(x => x.ServerIp == serverIp && x.Email == email && x.Date >= limitDate)
            .OrderByDescending(x => x.Date)
            .ToListAsync(token);
    }

    public async Task ResetDailyRiskAsync(string serverIp, string email, CancellationToken token = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var today = DateTime.Today;

        var log = await db.BehaviorLogs.FirstOrDefaultAsync(x => x.ServerIp == serverIp && x.Email == email && x.Date == today, token);
        if (log != null)
        {
            log.RiskScore = 0;
            log.MaxConcurrentSessions = 0;
            log.UniqueAsnCount = 0;
            log.GeoJumpsCount = 0;
            log.BytesUsedSpike = 0;
            await db.SaveChangesAsync(token);
        }

        ClearInMemCachesForNewDay(email);
        _logger.Log("ANTIFRAUD", $"Сброшены суточные метрики риска для {email} на {serverIp}");
    }

    public async Task ExecuteMonthlyRetentionPolicyAsync(CancellationToken token = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var oldDate = DateTime.Today.AddDays(-30);

        var oldLogs = await db.BehaviorLogs.Where(x => x.Date < oldDate).ToListAsync(token);
        if (oldLogs.Any())
        {
            db.BehaviorLogs.RemoveRange(oldLogs);
            await db.SaveChangesAsync(token);
            _logger.Log("ANTIFRAUD-CLEANUP", $"Удалено {oldLogs.Count} старых записей антифрода.");
        }
    }

    private void ClearInMemCachesForNewDay(string email)
    {
        _dailyAsns.TryRemove(email, out _);
        _lastCountryCode.TryRemove(email, out _);
        _lastCountryTime.TryRemove(email, out _);
    }
}