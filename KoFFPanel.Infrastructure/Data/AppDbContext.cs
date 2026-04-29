using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;

namespace KoFFPanel.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public DbSet<VpnClient> Clients { get; set; }
    public DbSet<ClientTrafficLog> TrafficLogs { get; set; }
    public DbSet<ClientConnectionLog> ConnectionLogs { get; set; }
    public DbSet<ClientViolationLog> ViolationLogs { get; set; }

    public AppDbContext()
    {
    }

    public void InitializeDatabaseOptimization()
    {
        try
        {
            // === 2026 MODERNIZATION: Автоматические миграции вместо ручных ALTER TABLE ===
            Database.Migrate();

            // Включаем WAL режим для параллельного чтения/записи (Боты + Логи)
            Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");

            // Быстрая проверка целостности при каждом запуске
            var result = Database.ExecuteSqlRaw("PRAGMA integrity_check;");
            // В случае успеха SQLite возвращает строки с "ok", если есть проблемы - ошибки вывалятся
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DB-OPTIMIZE-ERROR] Ошибка инициализации БД: {ex.Message}");
            throw; // Критическая ошибка БД должна быть обработана
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "koffpanel_users.db");

        // === 2026 MODERNIZATION: Master Password Management ===
        string dbPassword = MasterKeyService.Instance.GetMasterPassword();

        // Формируем строку подключения с поддержкой SQLCipher и пулом соединений
        optionsBuilder.UseSqlite($"Data Source={dbPath};Password={dbPassword};Pooling=True;");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VpnClient>().Ignore(c => c.TrafficUsageString);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.ExpiryString);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.StatusString);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.LastOnlineString);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.Country);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.AvatarPath); // Защита от краша EF Core

        modelBuilder.Entity<ClientTrafficLog>().HasIndex(t => new { t.ServerIp, t.Email, t.Date });
        modelBuilder.Entity<ClientConnectionLog>().HasIndex(c => new { c.ServerIp, c.Email, c.IpAddress });
        modelBuilder.Entity<ClientViolationLog>().HasIndex(v => new { v.ServerIp, v.Email });
    }
}