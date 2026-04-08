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
    public DbSet<ClientViolationLog> ViolationLogs { get; set; } // НОВАЯ ТАБЛИЦА

    public AppDbContext()
    {
        Database.EnsureCreated();

        try
        {
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

                -- НОВАЯ ТАБЛИЦА НАРУШЕНИЙ
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
        catch { }
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

        modelBuilder.Entity<ClientTrafficLog>().HasIndex(t => new { t.ServerIp, t.Email, t.Date });
        modelBuilder.Entity<ClientConnectionLog>().HasIndex(c => new { c.ServerIp, c.Email, c.IpAddress });
        modelBuilder.Entity<ClientViolationLog>().HasIndex(v => new { v.ServerIp, v.Email });
    }
}