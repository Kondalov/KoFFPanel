using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public record VpnLogEntry(string ServerIp, Dictionary<string, long> TrafficDeltas, List<(string Email, string Ip, string Country)> Connections, List<(string Email, string ViolationType)> Violations);

/// <summary>
/// Асинхронный буфер для логов. Собирает данные в очередь и записывает в БД пачками,
/// предотвращая блокировки SQLite при высокой нагрузке.
/// </summary>
public class LogBufferService : BackgroundService
{
    private readonly Channel<VpnLogEntry> _logChannel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LogBufferService> _logger;

    public LogBufferService(IServiceScopeFactory scopeFactory, ILogger<LogBufferService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        // Ограниченная очередь на 10 000 записей, чтобы не переполнить память
        _logChannel = Channel.CreateBounded<VpnLogEntry>(new BoundedChannelOptions(10000) { FullMode = BoundedChannelFullMode.Wait });
    }

    public bool TryWrite(VpnLogEntry entry) => _logChannel.Writer.TryWrite(entry);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[LOG-BUFFER] Сервис буферизации логов запущен.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ждем появления данных в канале
                if (await _logChannel.Reader.WaitToReadAsync(stoppingToken))
                {
                    // Делаем небольшую паузу, чтобы собрать пачку данных (Batching)
                    await Task.Delay(5000, stoppingToken);

                    var entries = new List<VpnLogEntry>();
                    while (_logChannel.Reader.TryRead(out var entry))
                    {
                        entries.Add(entry);
                        if (entries.Count >= 500) break; // Лимит пачки за один раз
                    }

                    if (entries.Count > 0)
                    {
                        await ProcessBatchAsync(entries);
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _logger.LogError($"[LOG-BUFFER-ERROR] {ex.Message}");
                await Task.Delay(5000, stoppingToken);
            }
        }
    }

    private async Task ProcessBatchAsync(List<VpnLogEntry> entries)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var today = DateTime.Today;

        _logger.LogDebug($"[LOG-BUFFER] Обработка пачки из {entries.Count} запросов на лог.");

        foreach (var entry in entries)
        {
            // Здесь мы могли бы оптимизировать еще сильнее, сгруппировав все entries по ServerIp/Email,
            // но для SQLite в WAL режиме даже такая пакетная обработка в одном SaveChanges — это уже огромный плюс.

            // 1. Трафик
            foreach (var t in entry.TrafficDeltas)
            {
                // Используем упрощенный поиск. В 2026 мы знаем, что индексы работают быстро.
                var log = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                    db.TrafficLogs, x => x.ServerIp == entry.ServerIp && x.Email == t.Key && x.Date == today);

                if (log != null) log.BytesUsed += t.Value;
                else db.TrafficLogs.Add(new ClientTrafficLog { ServerIp = entry.ServerIp, Email = t.Key, Date = today, BytesUsed = t.Value });
            }

            // 2. Подключения
            foreach (var c in entry.Connections)
            {
                var log = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstOrDefaultAsync(
                    db.ConnectionLogs, x => x.ServerIp == entry.ServerIp && x.Email == c.Email && x.IpAddress == c.Ip);

                if (log != null)
                {
                    log.LastSeen = DateTime.Now;
                    if (log.Country == "??" && c.Country != "??") log.Country = c.Country;
                }
                else db.ConnectionLogs.Add(new ClientConnectionLog { ServerIp = entry.ServerIp, Email = c.Email, IpAddress = c.Ip, Country = c.Country, FirstSeen = DateTime.Now, LastSeen = DateTime.Now });
            }

            // 3. Нарушения
            foreach (var v in entry.Violations)
            {
                db.ViolationLogs.Add(new ClientViolationLog { ServerIp = entry.ServerIp, Email = v.Email, Date = DateTime.Now, ViolationType = v.ViolationType });
            }
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError($"[LOG-BUFFER-DB-ERROR] Ошибка записи пачки: {ex.Message}");
        }
    }
}