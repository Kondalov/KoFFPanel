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

    public static string GetDatabasePath()
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
#if DEBUG
        string dbFileName = "koffpanel_users_dev.db";
        string configFolderName = "KoFFPanel_Dev";
#else
        string dbFileName = "koffpanel_users.db";
        string configFolderName = "KoFFPanel";
#endif
        string basePath = Path.Combine(appDataPath, configFolderName);
        if (!Directory.Exists(basePath))
        {
            Directory.CreateDirectory(basePath);
        }
        return Path.Combine(basePath, dbFileName);
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        if (!optionsBuilder.IsConfigured)
        {
            string dbPath = GetDatabasePath();
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
            Database.ExecuteSqlRaw("PRAGMA integrity_check;");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 26)
        {
            // SQLite Error 26: 'file is not a database'.
            // The database might have been encrypted with the legacy fixed master password.
            if (TryMigrateLegacyPassword())
            {
                Database.Migrate();
                Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
                Database.ExecuteSqlRaw("PRAGMA synchronous=NORMAL;");
                Database.ExecuteSqlRaw("PRAGMA integrity_check;");
            }
            else
            {
                throw;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DB-OPTIMIZE-ERROR] Ошибка: {ex.Message}");
            throw;
        }
    }

    private bool TryMigrateLegacyPassword()
    {
        try
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            var conn = Database.GetDbConnection();
            var builder = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(conn.ConnectionString)
            {
                Password = MasterKeyService.LegacyMasterPassword,
                Pooling = false
            };

            using var legacyConn = new Microsoft.Data.Sqlite.SqliteConnection(builder.ConnectionString);
            legacyConn.Open();
            using var cmd = legacyConn.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM sqlite_master;";
            cmd.ExecuteScalar();

            string newPassword = MasterKeyService.Instance.GetMasterPassword();
            cmd.CommandText = $"PRAGMA rekey = '{newPassword.Replace("'", "''")}';";
            cmd.ExecuteNonQuery();

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return true;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DB-REKEY-ERROR] Не удалось мигрировать ключ: {ex.Message}");
            return false;
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