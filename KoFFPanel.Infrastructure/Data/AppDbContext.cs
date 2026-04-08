using KoFFPanel.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;

namespace KoFFPanel.Infrastructure.Data;

public class AppDbContext : DbContext
{
    // Таблица наших пользователей
    public DbSet<VpnClient> Clients { get; set; }

    public AppDbContext()
    {
        // Умный режим: Автоматически создаем файл базы данных при первом запуске
        Database.EnsureCreated();
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // База данных будет лежать рядом с .exe файлом
        string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "koffpanel_users.db");
        optionsBuilder.UseSqlite($"Data Source={dbPath}");
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // Указываем Entity Framework игнорировать вычисляемые UI-свойства, 
        // чтобы они не создавали лишних колонок в базе данных
        modelBuilder.Entity<VpnClient>().Ignore(c => c.TrafficUsageString);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.ExpiryString);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.StatusString);
        modelBuilder.Entity<VpnClient>().Ignore(c => c.LastOnlineString);

        // ИСПРАВЛЕНИЕ: Игнорируем новую колонку страны, так как она вычисляется на лету
        modelBuilder.Entity<VpnClient>().Ignore(c => c.Country);
    }
}