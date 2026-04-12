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

        // Строго отсекаем время, оставляем только дату для идеального поиска
        var today = DateTime.Today;

        try
        {
            _logger.Log("ANALYTICS-DB", $"[START] Сохранение батча. Трафик: {trafficDeltas.Count}, IP: {connections.Count}, Нарушения: {violations.Count}");

            // 1. Умное сохранение трафика
            foreach (var t in trafficDeltas)
            {
                var log = await db.TrafficLogs.FirstOrDefaultAsync(x => x.ServerIp == serverIp && x.Email == t.Key && x.Date.Year == today.Year && x.Date.Month == today.Month && x.Date.Day == today.Day);
                if (log != null)
                {
                    log.BytesUsed += t.Value;
                }
                else
                {
                    db.TrafficLogs.Add(new ClientTrafficLog { ServerIp = serverIp, Email = t.Key, Date = today, BytesUsed = t.Value });
                    _logger.Log("ANALYTICS-DB", $"Создана новая запись трафика для {t.Key} на дату {today:yyyy-MM-dd}");
                }
            }

            // 2. Умное сохранение подключений (Устройств)
            foreach (var c in connections)
            {
                var log = await db.ConnectionLogs.FirstOrDefaultAsync(x => x.ServerIp == serverIp && x.Email == c.Email && x.IpAddress == c.Ip);
                if (log != null)
                {
                    log.LastSeen = DateTime.Now;
                    if (log.Country == "??" && c.Country != "??") log.Country = c.Country; // Обновляем локацию, если она появилась
                }
                else
                {
                    db.ConnectionLogs.Add(new ClientConnectionLog { ServerIp = serverIp, Email = c.Email, IpAddress = c.Ip, Country = c.Country, FirstSeen = DateTime.Now, LastSeen = DateTime.Now });
                    _logger.Log("ANALYTICS-DB", $"Зафиксирован новый IP {c.Ip} для пользователя {c.Email}");
                }
            }

            // 3. Запись нарушений (защита от спама - 1 раз в час)
            var oneHourAgo = DateTime.Now.AddHours(-1);
            foreach (var v in violations)
            {
                // Используем AsNoTracking для быстрого поиска (по правилам Senior чек-листа)
                var recentViol = await db.ViolationLogs.AsNoTracking().FirstOrDefaultAsync(x => x.ServerIp == serverIp && x.Email == v.Email && x.ViolationType == v.ViolationType && x.Date > oneHourAgo);
                if (recentViol == null)
                {
                    db.ViolationLogs.Add(new ClientViolationLog { ServerIp = serverIp, Email = v.Email, Date = DateTime.Now, ViolationType = v.ViolationType });
                    _logger.Log("ANALYTICS-DB", $"🚨 Зафиксировано новое нарушение для {v.Email}: {v.ViolationType}");
                }
            }

            await db.SaveChangesAsync();
            _logger.Log("ANALYTICS-DB", "[SUCCESS] Батч успешно записан в базу.");
        }
        catch (Exception ex)
        {
            _logger.Log("ANALYTICS-ERR", $"КРИТИЧЕСКАЯ Ошибка сохранения: {ex.Message}");
        }
    }

    public async Task<List<ClientTrafficLog>> GetTrafficLogsAsync(string serverIp, string email, int days = 30)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var limitDate = DateTime.Today.AddDays(-days);

        // ЗАЩИТА ОТ ДУРАКА: SQLite криво сравнивает даты в формате TEXT.
        // Выгружаем логи юзера (их не больше 30) в память и фильтруем через строгий C#!
        var allUserLogs = await db.TrafficLogs.AsNoTracking().Where(x => x.ServerIp == serverIp && x.Email == email).ToListAsync();

        var filtered = allUserLogs.Where(x => x.Date >= limitDate).OrderByDescending(x => x.Date).ToList();
        _logger.Log("ANALYTICS-READ", $"Загружено {filtered.Count} логов трафика для {email}");
        return filtered;
    }

    public async Task<List<ClientConnectionLog>> GetConnectionLogsAsync(string serverIp, string email)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var logs = await db.ConnectionLogs.AsNoTracking().Where(x => x.ServerIp == serverIp && x.Email == email).OrderByDescending(x => x.LastSeen).ToListAsync();
        _logger.Log("ANALYTICS-READ", $"Загружено {logs.Count} уникальных IP для {email}");
        return logs;
    }

    public async Task<List<ClientViolationLog>> GetViolationLogsAsync(string serverIp, string email)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var limitDate = DateTime.Today.AddDays(-30);
        var allLogs = await db.ViolationLogs.AsNoTracking().Where(x => x.ServerIp == serverIp && x.Email == email).ToListAsync();

        var filtered = allLogs.Where(x => x.Date >= limitDate).OrderByDescending(x => x.Date).ToList();
        return filtered;
    }

    public async Task CleanupOldLogsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Унифицируем срок хранения - ровно 30 дней для всего.
        var thresholdDate = DateTime.Today.AddDays(-30);

        try
        {
            // Фильтруем в памяти, чтобы избежать крашей SQLite (TEXT Date comparison)
            var oldTraffic = await db.TrafficLogs.ToListAsync();
            var trafficToRemove = oldTraffic.Where(x => x.Date < thresholdDate).ToList();
            db.TrafficLogs.RemoveRange(trafficToRemove);

            var oldConnections = await db.ConnectionLogs.ToListAsync();
            var connToRemove = oldConnections.Where(x => x.LastSeen < thresholdDate).ToList();
            db.ConnectionLogs.RemoveRange(connToRemove);

            var oldViolations = await db.ViolationLogs.ToListAsync();
            var violToRemove = oldViolations.Where(x => x.Date < thresholdDate).ToList();
            db.ViolationLogs.RemoveRange(violToRemove);

            if (trafficToRemove.Any() || connToRemove.Any() || violToRemove.Any())
            {
                await db.SaveChangesAsync();
                _logger.Log("ANALYTICS-CLEANUP", $"♻️ Очистка завершена. Удалено: Трафик({trafficToRemove.Count}), IP({connToRemove.Count}), Нарушения({violToRemove.Count}).");
            }
        }
        catch (Exception ex)
        {
            _logger.Log("ANALYTICS-ERR", $"Ошибка при автоочистке логов: {ex.Message}");
        }
    }
}