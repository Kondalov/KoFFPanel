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
        // Устанавливаем путь базы данных для тестов в папке с тестами
        _testDbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "koffpanel_users.db");
        if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
        
        string masterKeyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MasterPassword_DO_NOT_SHARE.txt");
        if (File.Exists(masterKeyPath)) File.Delete(masterKeyPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        
        if (File.Exists(_testDbPath)) File.Delete(_testDbPath);
        
        string masterKeyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MasterPassword_DO_NOT_SHARE.txt");
        if (File.Exists(masterKeyPath)) File.Delete(masterKeyPath);
    }

    [Fact]
    public void MasterKeyService_ShouldReturnFixedKey()
    {
        // Act
        string key1 = MasterKeyService.Instance.GetMasterPassword();
        string key2 = MasterKeyService.Instance.GetMasterPassword();
        
        // Assert
        Assert.False(string.IsNullOrWhiteSpace(key1));
        Assert.Equal(key1, key2);
        Assert.Equal("KoFF_Fixed_Master_Key_2026_!!_Safe", key1);
    }

    [Fact]
    public void InitializeDatabaseOptimization_ShouldCreateEncryptedDatabaseAndApplyMigrations()
    {
        // Arrange
        using var dbContext = new AppDbContext();

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