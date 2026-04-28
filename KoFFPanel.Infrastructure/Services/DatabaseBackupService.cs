using KoFFPanel.Application.Interfaces;
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
/// Реализует паттерн Zero Trust: копия делается ДО того, как EF Core начнет работу.
/// </summary>
public class DatabaseBackupService : IHostedLifecycleService, IDatabaseBackupService
{
    private readonly ILogger<DatabaseBackupService> _logger;
    private const int MaxBackupCount = 7;

    public DatabaseBackupService(ILogger<DatabaseBackupService> logger)
    {
        _logger = logger;
    }

    // Реализация IDatabaseBackupService для совместимости с CabinetViewModel
    public async Task CreateBackupAsync()
    {
        await PerformBackupAsync();
    }

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        return PerformBackupAsync();
    }

    private Task PerformBackupAsync()
    {
        try
        {
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "koffpanel_users.db");
            if (!File.Exists(dbPath)) return Task.CompletedTask;

            string backupDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
            if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);

            // Очистка старых бэкапов (оставляем только последние 7)
            var oldBackups = new DirectoryInfo(backupDir)
                .GetFiles("*.bak")
                .OrderByDescending(f => f.CreationTime)
                .Skip(MaxBackupCount);

            foreach (var file in oldBackups) file.Delete();

            // Создание нового бэкапа
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string backupPath = Path.Combine(backupDir, $"koffpanel_{timestamp}.bak");
            
            File.Copy(dbPath, backupPath, true);
            _logger.LogInformation($"[DB-BACKUP] Создана резервная копия: {backupPath}");
        }
        catch (Exception ex)
        {
            _logger.LogError($"[DB-BACKUP-ERROR] Ошибка при создании бэкапа: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}