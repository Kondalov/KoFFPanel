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
    private readonly LogBufferService _logBuffer;

    public ClientAnalyticsService(IServiceScopeFactory scopeFactory, IAppLogger logger, LogBufferService logBuffer)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _logBuffer = logBuffer;
    }

    public async Task SaveBatchAsync(string serverIp, Dictionary<string, long> trafficDeltas, List<(string Email, string Ip, string Country)> connections, List<(string Email, string ViolationType)> violations)
    {
        if (!trafficDeltas.Any() && !connections.Any() && !violations.Any()) return;

        // 2026 MODERNIZATION: Вместо прямого сохранения в БД, кидаем в асинхронный буфер.
        // Это мгновенно освобождает поток UI/Логики и предотвращает Database Locked.
        var entry = new VpnLogEntry(serverIp, trafficDeltas, connections, violations);

        if (_logBuffer.TryWrite(entry))
        {
            _logger.Log("ANALYTICS-ASYNC", $"Батч ({trafficDeltas.Count} записей) отправлен в очередь на сохранение.");
        }
        else
        {
            _logger.Log("ANALYTICS-ASYNC-WARN", "Очередь логов переполнена! Данные могут быть потеряны.");
        }

        await Task.CompletedTask;
    }

    public async Task<List<ClientTrafficLog>> GetTrafficLogsAsync(string serverIp, string email, int days = 30)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var limitDate = DateTime.Today.AddDays(-days);

        var logs = await db.TrafficLogs
            .AsNoTracking()
            .Where(x => x.ServerIp == serverIp && x.Email == email && x.Date >= limitDate)
            .OrderByDescending(x => x.Date)
            .ToListAsync();
        _logger.Log("ANALYTICS-READ", $"Загружено {logs.Count} логов трафика для {email}");
        return logs;
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

        var logs = await db.ViolationLogs
            .AsNoTracking()
            .Where(x => x.ServerIp == serverIp && x.Email == email && x.Date >= limitDate)
            .OrderByDescending(x => x.Date)
            .ToListAsync();
        return logs;
    }

    public async Task CleanupOldLogsAsync()
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Унифицируем срок хранения - ровно 30 дней для всего.
        var thresholdDate = DateTime.Today.AddDays(-30);

        try
        {
            // Используем ExecuteDeleteAsync для мгновенного удаления на стороне БД без загрузки в память
            int deletedTraffic = await db.TrafficLogs.Where(x => x.Date < thresholdDate).ExecuteDeleteAsync();
            int deletedConnections = await db.ConnectionLogs.Where(x => x.LastSeen < thresholdDate).ExecuteDeleteAsync();
            int deletedViolations = await db.ViolationLogs.Where(x => x.Date < thresholdDate).ExecuteDeleteAsync();

            if (deletedTraffic > 0 || deletedConnections > 0 || deletedViolations > 0)
            {
                _logger.Log("ANALYTICS-CLEANUP", $"♻️ Очистка завершена. Удалено: Трафик({deletedTraffic}), IP({deletedConnections}), Нарушения({deletedViolations}).");
            }
        }
        catch (Exception ex)
        {
            _logger.Log("ANALYTICS-ERR", $"Ошибка при автоочистке логов: {ex.Message}");
        }
    }
}