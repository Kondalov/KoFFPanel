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

        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string basePath = Path.Combine(appDataPath, "KoFFPanel_Dev");

        if (!Directory.Exists(basePath))
        {
            Directory.CreateDirectory(basePath);
        }

        string dbPath = Path.Combine(basePath, "koffpanel_users_dev.db");
        string dbPassword = MasterKeyService.Instance.GetMasterPassword();

        optionsBuilder.UseSqlite($"Data Source={dbPath};Password={dbPassword};Pooling=True;");

        return new AppDbContext(optionsBuilder.Options);
    }
}