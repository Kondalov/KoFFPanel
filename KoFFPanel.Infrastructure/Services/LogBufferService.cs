using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
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

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logChannel.Writer.TryComplete();
        var remaining = new List<VpnLogEntry>();
        while (_logChannel.Reader.TryRead(out var entry))
        {
            remaining.Add(entry);
        }

        if (remaining.Count > 0)
        {
            try
            {
                await ProcessBatchAsync(remaining);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[LOG-BUFFER-STOP-ERROR] {ex.Message}");
            }
        }

        await base.StopAsync(cancellationToken);
    }

    private async Task ProcessBatchAsync(List<VpnLogEntry> entries)
    {
        if (entries == null || entries.Count == 0) return;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var today = DateTime.Today;

        _logger.LogDebug($"[LOG-BUFFER] Обработка пачки из {entries.Count} запросов на лог.");

        // 1. Агрегация трафика в памяти (устранение N+1)
        var trafficAggregated = new Dictionary<(string ServerIp, string Email), long>();
        foreach (var entry in entries)
        {
            foreach (var t in entry.TrafficDeltas)
            {
                var key = (entry.ServerIp, t.Key);
                trafficAggregated[key] = trafficAggregated.GetValueOrDefault(key) + t.Value;
            }
        }

        if (trafficAggregated.Count > 0)
        {
            var serverIps = trafficAggregated.Keys.Select(k => k.ServerIp).Distinct().ToList();
            var emails = trafficAggregated.Keys.Select(k => k.Email).Distinct().ToList();

            var existingTraffic = await db.TrafficLogs
                .Where(x => x.Date == today && serverIps.Contains(x.ServerIp) && emails.Contains(x.Email))
                .ToListAsync();

            var existingMap = existingTraffic.ToDictionary(x => (x.ServerIp, x.Email));

            foreach (var (key, bytes) in trafficAggregated)
            {
                if (existingMap.TryGetValue(key, out var log))
                {
                    log.BytesUsed += bytes;
                }
                else
                {
                    var newLog = new ClientTrafficLog
                    {
                        ServerIp = key.ServerIp,
                        Email = key.Email,
                        Date = today,
                        BytesUsed = bytes
                    };
                    db.TrafficLogs.Add(newLog);
                    existingMap[key] = newLog;
                }
            }
        }

        // 2. Агрегация подключений в памяти (устранение N+1)
        var connAggregated = new Dictionary<(string ServerIp, string Email, string Ip), string>();
        foreach (var entry in entries)
        {
            foreach (var c in entry.Connections)
            {
                var key = (entry.ServerIp, c.Email, c.Ip);
                if (!connAggregated.TryGetValue(key, out var prevCountry) || (prevCountry == "??" && c.Country != "??"))
                {
                    connAggregated[key] = c.Country;
                }
            }
        }

        if (connAggregated.Count > 0)
        {
            var serverIps = connAggregated.Keys.Select(k => k.ServerIp).Distinct().ToList();
            var emails = connAggregated.Keys.Select(k => k.Email).Distinct().ToList();
            var ips = connAggregated.Keys.Select(k => k.Ip).Distinct().ToList();

            var existingConns = await db.ConnectionLogs
                .Where(x => serverIps.Contains(x.ServerIp) && emails.Contains(x.Email) && ips.Contains(x.IpAddress))
                .ToListAsync();

            var existingMap = existingConns.ToDictionary(x => (x.ServerIp, x.Email, x.IpAddress));
            var now = DateTime.Now;

            foreach (var (key, country) in connAggregated)
            {
                if (existingMap.TryGetValue(key, out var log))
                {
                    log.LastSeen = now;
                    if (log.Country == "??" && country != "??")
                    {
                        log.Country = country;
                    }
                }
                else
                {
                    var newLog = new ClientConnectionLog
                    {
                        ServerIp = key.ServerIp,
                        Email = key.Email,
                        IpAddress = key.Ip,
                        Country = country,
                        FirstSeen = now,
                        LastSeen = now
                    };
                    db.ConnectionLogs.Add(newLog);
                    existingMap[key] = newLog;
                }
            }
        }

        // 3. Нарушения
        var violationsToAdd = new List<ClientViolationLog>();
        foreach (var entry in entries)
        {
            foreach (var v in entry.Violations)
            {
                violationsToAdd.Add(new ClientViolationLog
                {
                    ServerIp = entry.ServerIp,
                    Email = v.Email,
                    Date = DateTime.Now,
                    ViolationType = v.ViolationType
                });
            }
        }

        if (violationsToAdd.Count > 0)
        {
            db.ViolationLogs.AddRange(violationsToAdd);
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, $"[LOG-BUFFER-DB-ERROR] Ошибка записи пачки: {ex.Message}");
        }
    }
}