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

    // ДОБАВЛЕНА НОВАЯ ТАБЛИЦА ФРОД-СКОРИНГА
    public DbSet<ClientBehaviorLog> BehaviorLogs { get; set; }

    public AppDbContext() { }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "koffpanel_users.db");
            string dbPassword = MasterKeyService.Instance.GetMasterPassword();
            optionsBuilder.UseSqlite($"Data Source={dbPath};Password={dbPassword};Pooling=True;");
        }
    }

    public void InitializeDatabaseOptimization()
    {
        try
        {
            Database.Migrate();
            Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
            var result = Database.ExecuteSqlRaw("PRAGMA integrity_check;");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DB-OPTIMIZE-ERROR] Ошибка: {ex.Message}");
            throw;
        }
    }

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<VpnClient>().Ignore(c => c.TrafficUsageString);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.ExpiryString);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.StatusString);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.LastOnlineString);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.Country);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.AvatarPath);

        modelBuilder.Entity<ClientTrafficLog>().HasIndex(t => new { t.ServerIp, t.Email, t.Date });
        modelBuilder.Entity<ClientConnectionLog>().HasIndex(c => new { c.ServerIp, c.Email, c.IpAddress });
        modelBuilder.Entity<ClientViolationLog>().HasIndex(v => new { v.ServerIp, v.Email });

        // Индекс для быстрой выборки аналитики за месяц
        modelBuilder.Entity<ClientBehaviorLog>().HasIndex(b => new { b.ServerIp, b.Email, b.Date }).IsUnique();
    }
}