using KoFFPanel.Application.Interfaces;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

/// <summary>
/// Сервис для автоматического резервного копирования базы данных перед запуском приложения.
/// Реализует паттерн Zero Trust: потокобезопасная копия делается через VACUUM INTO с поддержкой WAL.
/// </summary>
public class DatabaseBackupService : IHostedLifecycleService, IDatabaseBackupService
{
    private readonly ILogger<DatabaseBackupService> _logger;
    private const int MaxBackupCount = 7;

    public DatabaseBackupService(ILogger<DatabaseBackupService> logger)
    {
        _logger = logger;
    }

    public async Task CreateBackupAsync()
    {
        await PerformBackupAsync();
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        return PerformBackupAsync();
    }

    private async Task PerformBackupAsync()
    {
        try
        {
            string dbPath = AppDbContext.GetDatabasePath();
            if (!File.Exists(dbPath)) return;

            string appDataFolder = Path.GetDirectoryName(dbPath)!;
            string backupDir = Path.Combine(appDataFolder, "Backups");
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            // Очистка старых бэкапов
            var oldBackups = new DirectoryInfo(backupDir)
                .GetFiles("*.bak")
                .OrderByDescending(f => f.CreationTime)
                .Skip(MaxBackupCount);

            foreach (var file in oldBackups)
            {
                try { file.Delete(); } catch { }
            }

            // Создание нового бэкапа
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string backupPath = Path.Combine(backupDir, $"koffpanel_{timestamp}.bak");

            // Потокобезопасный механизм снапшотов SQLite в WAL-режиме
            using (var db = new AppDbContext())
            {
                string safeBackupPath = backupPath.Replace("'", "''");
#pragma warning disable EF1002
                await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{safeBackupPath}';");
#pragma warning restore EF1002
            }

            _logger.LogInformation($"[DB-BACKUP] Создана резервная копия через VACUUM INTO: {backupPath}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"[DB-BACKUP-ERROR] Ошибка при создании бэкапа: {ex.Message}");
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}