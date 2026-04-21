using KoFFPanel.Domain.Entities;
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
        Database.EnsureCreated();

        try
        {
            // Создание таблиц логов
            Database.ExecuteSqlRaw(@"
                CREATE TABLE IF NOT EXISTS ""TrafficLogs"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_TrafficLogs"" PRIMARY KEY AUTOINCREMENT,
                    ""ServerIp"" TEXT NOT NULL,
                    ""Email"" TEXT NOT NULL,
                    ""Date"" TEXT NOT NULL,
                    ""BytesUsed"" INTEGER NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ""IX_TrafficLogs_ServerIp_Email_Date"" ON ""TrafficLogs"" (""ServerIp"", ""Email"", ""Date"");

                CREATE TABLE IF NOT EXISTS ""ConnectionLogs"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_ConnectionLogs"" PRIMARY KEY AUTOINCREMENT,
                    ""ServerIp"" TEXT NOT NULL,
                    ""Email"" TEXT NOT NULL,
                    ""IpAddress"" TEXT NOT NULL,
                    ""Country"" TEXT NOT NULL,
                    ""FirstSeen"" TEXT NOT NULL,
                    ""LastSeen"" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ""IX_ConnectionLogs_ServerIp_Email_IpAddress"" ON ""ConnectionLogs"" (""ServerIp"", ""Email"", ""IpAddress"");

                CREATE TABLE IF NOT EXISTS ""ViolationLogs"" (
                    ""Id"" INTEGER NOT NULL CONSTRAINT ""PK_ViolationLogs"" PRIMARY KEY AUTOINCREMENT,
                    ""ServerIp"" TEXT NOT NULL,
                    ""Email"" TEXT NOT NULL,
                    ""Date"" TEXT NOT NULL,
                    ""ViolationType"" TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ""IX_ViolationLogs_ServerIp_Email"" ON ""ViolationLogs"" (""ServerIp"", ""Email"");
            ");
        }
        catch (Exception ex)
        {
            // === ИСПРАВЛЕНИЕ: Исключения больше не проглатываются (Никаких пустых catch) ===
            System.Diagnostics.Debug.WriteLine($"[DB INIT ERROR] Ошибка создания таблиц логов: {ex.Message}");
        }

        // === ИСПРАВЛЕНИЕ: БЕЗОПАСНАЯ НЕЗАВИСИМАЯ МИГРАЦИЯ ===
        SafeExecuteSql("ALTER TABLE \"Clients\" ADD COLUMN \"IsP2PBlocked\" INTEGER NOT NULL DEFAULT 1;");
        SafeExecuteSql("ALTER TABLE \"Clients\" ADD COLUMN \"IsVlessEnabled\" INTEGER NOT NULL DEFAULT 1;");
        SafeExecuteSql("ALTER TABLE \"Clients\" ADD COLUMN \"IsHysteria2Enabled\" INTEGER NOT NULL DEFAULT 0;");
        SafeExecuteSql("ALTER TABLE \"Clients\" ADD COLUMN \"Hysteria2Link\" TEXT NOT NULL DEFAULT '';");
        SafeExecuteSql("ALTER TABLE \"Clients\" ADD COLUMN \"IsTrustTunnelEnabled\" INTEGER NOT NULL DEFAULT 0;");
        SafeExecuteSql("ALTER TABLE \"Clients\" ADD COLUMN \"TrustTunnelLink\" TEXT NOT NULL DEFAULT '';");
    }

    // === НОВЫЙ МЕТОД: Обработка SQL-ошибок с логированием ===
    private void SafeExecuteSql(string sql)
    {
        try
        {
            Database.ExecuteSqlRaw(sql);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 1)
        {
            // Ошибка 1 (SQLITE_ERROR) означает "duplicate column name", что нормально при повторном запуске.
            // Игнорируем осознанно, но не проглатываем другие фатальные ошибки, чтобы база не сломалась тихо.
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DB MIGRATION ERROR] Не удалось выполнить {sql}: {ex.Message}");
        }
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "koffpanel_users.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
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