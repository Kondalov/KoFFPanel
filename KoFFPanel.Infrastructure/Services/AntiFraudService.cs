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

        if (client.ActiveConnections > log.MaxConcurrentSessions)
            log.MaxConcurrentSessions = client.ActiveConnections;

        string subnetAsn = GetMockAsnFromIp(currentIp);
        if (!_dailyAsns.ContainsKey(email)) _dailyAsns[email] = new HashSet<string>();
        if (!string.IsNullOrEmpty(subnetAsn) && _dailyAsns[email].Add(subnetAsn))
        {
            log.UniqueAsnCount = _dailyAsns[email].Count;
        }

        // Вызов нового, защищенного метода проверки геолокации
        UpdateGeoMetrics(log, email, client.Country);

        if (trafficDelta > 1073741824L)
            log.BytesUsedSpike += trafficDelta;
    }

    private void UpdateGeoMetrics(ClientBehaviorLog log, string email, string? rawCountry)
    {
        string curCode = GetValidCountryCode(rawCountry);

        // Если страна не определилась (например, мобильный интернет без Geo-данных), просто игнорируем
        if (string.IsNullOrEmpty(curCode)) return;

        // Если это первое подключение юзера, просто запоминаем его страну
        if (!_lastCountryCode.TryGetValue(email, out string? lastCode) || string.IsNullOrEmpty(lastCode))
        {
            _lastCountryCode[email] = curCode;
            _lastCountryTime[email] = DateTime.Now;
            return;
        }

        // Если страна РЕАЛЬНО сменилась на другую валидную страну
        if (lastCode != curCode)
        {
            if (_lastCountryTime.TryGetValue(email, out DateTime lastTime) && (DateTime.Now - lastTime).TotalHours < 2)
            {
                log.GeoJumpsCount++;
                _logger.Log("ANTIFRAUD", $"Зафиксирован GeoJump для {email}: {lastCode} -> {curCode}");
            }
            _lastCountryCode[email] = curCode;
        }

        // Всегда обновляем время последней активности для этой страны
        _lastCountryTime[email] = DateTime.Now;
    }

    private string GetValidCountryCode(string? rawCountry)
    {
        if (string.IsNullOrWhiteSpace(rawCountry)) return "";

        string code = rawCountry.Trim();
        if (code.Length >= 2) code = code.Substring(code.Length - 2).ToUpperInvariant();

        // Исключаем пустые значения, анонимные прокси и небуквенные символы
        if (code == "??" || code == "A1" || code == "O1" || !code.All(char.IsLetter))
            return "";

        return code;
    }

    private void CalculateRiskScore(ClientBehaviorLog log)
    {
        int score = 0;

        if (log.MaxConcurrentSessions > 8) score += (log.MaxConcurrentSessions - 8) * 10;
        if (log.UniqueAsnCount > 3) score += (log.UniqueAsnCount - 3) * 20;
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

    public async Task ResetDailyRiskAsync(string serverIp, string email, CancellationToken token = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var today = DateTime.Today;
        var log = await db.BehaviorLogs.FirstOrDefaultAsync(x => x.ServerIp == serverIp && x.Email == email && x.Date == today, token);

        if (log != null)
        {
            // Полный сброс метрик за день
            log.RiskScore = 0;
            log.MaxConcurrentSessions = 0;
            log.UniqueAsnCount = 0;
            log.GeoJumpsCount = 0;
            log.BytesUsedSpike = 0;

            await db.SaveChangesAsync(token);

            // Очищаем кэши в памяти, чтобы прыжки не засчитались сразу после сброса
            _dailyAsns.Remove(email);
            _lastCountryCode.Remove(email);
            _logger.Log("ANTIFRAUD-RESET", $"Риск-скоринг для {email} полностью сброшен администратором.");
        }
    }

    private void ClearInMemCachesForNewDay(string email)
    {
        _dailyAsns.Remove(email);
    }
}