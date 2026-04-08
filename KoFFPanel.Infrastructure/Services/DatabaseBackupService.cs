using KoFFPanel.Application.Interfaces;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class DatabaseBackupService : IDatabaseBackupService
{
    private readonly IAppLogger _logger;

    public DatabaseBackupService(IAppLogger logger)
    {
        _logger = logger;
    }

    public Task CreateBackupAsync()
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string dbPath = Path.Combine(baseDir, "koffpanel_users.db");
            string backupDir = Path.Combine(baseDir, "Backups");

            if (!File.Exists(dbPath)) return Task.CompletedTask;

            if (!Directory.Exists(backupDir))
            {
                Directory.CreateDirectory(backupDir);
            }

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string backupFileName = $"koffpanel_users_backup_{timestamp}.db";
            string backupPath = Path.Combine(backupDir, backupFileName);

            File.Copy(dbPath, backupPath, true);
            _logger.Log("BACKUP", $"Резервная копия БД создана: {backupFileName}");

            // Очистка старых бэкапов (храним только последние 7 дней)
            var directoryInfo = new DirectoryInfo(backupDir);
            var oldBackups = directoryInfo.GetFiles("*.db")
                .Where(f => f.CreationTime < DateTime.Now.AddDays(-7))
                .ToList();

            foreach (var oldBackup in oldBackups)
            {
                oldBackup.Delete();
            }
        }
        catch (Exception ex)
        {
            _logger.Log("BACKUP-ERROR", $"Ошибка создания резервной копии: {ex.Message}");
        }

        return Task.CompletedTask;
    }
}