using KoFFPanel.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System;
using System.IO;

namespace KoFFPanel.Infrastructure.Data;

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "koffpanel_users.db");
        string dbPassword = MasterKeyService.Instance.GetMasterPassword();

        optionsBuilder.UseSqlite($"Data Source={dbPath};Password={dbPassword};Pooling=True;");

        return new AppDbContext();
    }
}