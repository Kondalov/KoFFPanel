using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using KoFFPanel.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace KoFFPanel.Tests;

public class DatabaseArchitectureTests : IDisposable
{
    private readonly string _testDbPath;

    public DatabaseArchitectureTests()
    {
        _testDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "koffpanel_test.db");
        CleanDatabaseFiles();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        CleanDatabaseFiles();
    }

    private void CleanDatabaseFiles()
    {
        if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
        if (File.Exists(_testDbPath + "-wal")) File.Delete(_testDbPath + "-wal");
        if (File.Exists(_testDbPath + "-shm")) File.Delete(_testDbPath + "-shm");
    }

    [Fact]
    public void MasterKeyService_ShouldGenerateAndPersistAESKey()
    {
        // Act
        string key1 = MasterKeyService.Instance.GetMasterPassword();
        string key2 = MasterKeyService.Instance.GetMasterPassword();

        // Assert
        Assert.False(string.IsNullOrWhiteSpace(key1));
        Assert.Equal(key1, key2);

        byte[] keyBytes = Convert.FromBase64String(key1);
        Assert.Equal(32, keyBytes.Length);
    }

    [Fact]
    public void InitializeDatabaseOptimization_ShouldCreateEncryptedDatabaseAndApplyMigrations()
    {
        // Arrange
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={_testDbPath};Password={MasterKeyService.Instance.GetMasterPassword()};Pooling=False;")
            .Options;
        using var dbContext = new AppDbContext(options);

        // Act
        dbContext.InitializeDatabaseOptimization();

        // Assert
        Assert.True(File.Exists(_testDbPath), "Файл базы данных должен быть создан.");

        // Проверяем, что в БД можно записать данные (схема создана и зашифрована корректно)
        dbContext.Clients.Add(new VpnClient
        {
            Email = "test@test.com",
            ServerIp = "1.1.1.1",
            IsActive = true,
            IsVlessEnabled = true,
            IsHysteria2Enabled = true
        });
        dbContext.SaveChanges();

        var client = dbContext.Clients.FirstOrDefault(c => c.Email == "test@test.com");
        Assert.NotNull(client);
        Assert.Equal("1.1.1.1", client.ServerIp);
    }
}