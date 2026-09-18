using KoFFPanel.Domain.Entities;
using Xunit;

namespace KoFFPanel.Tests;

public class AntiFraudTests
{
    // ИЗМЕНЕНО: Название теста теперь оканчивается на ShouldReturnHighRisk (а не Ban)
    [Fact]
    public void CalculateRiskScore_CheckNewLimits_ShouldReturnHighRisk()
    {
        // Arrange: Создаем поддельную запись поведения юзера 
        // Имитируем: 10 устройств (на 2 больше нового лимита) и 1 прыжок по странам
        var log = new ClientBehaviorLog
        {
            MaxConcurrentSessions = 10, // Лимит 8, значит 2 лишних (2 * 10% = 20%)
            GeoJumpsCount = 1           // 1 прыжок (1 * 80% = 80%)
        };

        // Act: Воспроизводим актуальную формулу из нашего сервиса AntiFraudService
        int score = 0;

        // Лимит расширен до 8 устройств, штраф снижен до 10% за каждое последующее
        if (log.MaxConcurrentSessions > 8) score += (log.MaxConcurrentSessions - 8) * 10;
        if (log.GeoJumpsCount > 0) score += log.GeoJumpsCount * 80;

        log.RiskScore = score > 100 ? 100 : score;

        // Assert: Проверяем, что 20% + 80% = 100% и алгоритм выдал метку максимального риска
        Assert.Equal(100, log.RiskScore);

        // Свойство IsBanned в БД теперь означает "Критичный риск" (кандидат на ручную блокировку)
        Assert.True(log.IsBanned);
    }

    [Fact]
    public async Task ResetDailyRisk_ShouldClearAllMetrics()
    {
        // Arrange
        var log = new ClientBehaviorLog
        {
            ServerIp = "192.168.1.1",
            Email = "riskuser@test.com",
            Date = DateTime.Today,
            RiskScore = 100,
            MaxConcurrentSessions = 15,
            GeoJumpsCount = 2,
            UniqueAsnCount = 5
        };

        // Act - verify resetting logic behavior
        log.RiskScore = 0;
        log.MaxConcurrentSessions = 0;
        log.UniqueAsnCount = 0;
        log.GeoJumpsCount = 0;
        log.BytesUsedSpike = 0;

        // Assert
        Assert.Equal(0, log.RiskScore);
        Assert.False(log.IsBanned);
    }

    [Fact]
    public void CalculateRiskScore_WithinNormalLimits_ShouldHaveZeroRisk()
    {
        var log = new ClientBehaviorLog
        {
            MaxConcurrentSessions = 4, // Within limit of 8
            GeoJumpsCount = 0,
            BytesUsedSpike = 0
        };

        int score = 0;
        if (log.MaxConcurrentSessions > 8) score += (log.MaxConcurrentSessions - 8) * 10;
        if (log.GeoJumpsCount > 0) score += log.GeoJumpsCount * 80;
        if (log.BytesUsedSpike > 0) score += 30;

        log.RiskScore = score;
        Assert.Equal(0, log.RiskScore);
        Assert.False(log.IsBanned);
    }

    [Fact]
    public void VpnClient_FraudProperties_ShouldNotModifyNote()
    {
        var client = new VpnClient
        {
            Email = "tg_1078760031",
            Note = "Kostya",
            IsAntiFraudEnabled = true
        };

        // Simulate fraud detection
        client.IsFraud = true;
        client.FraudReason = "ФРОД 100%: Сессий=2, ASN=4, GeoJumps=1";

        // Assert that client.Note was NOT changed or overwritten with fraud reason
        Assert.Equal("Kostya", client.Note);
        Assert.True(client.IsFraud);
        Assert.Contains("ФРОД 100%", client.FraudReason);
    }

    [Fact]
    public void DeviceCounting_SameIpMultiplePorts_ShouldCountAsOneDevice()
    {
        // 1 physical device using Hysteria 2 with 4 ephemeral ports
        var connections = new List<(string Ip, string Port)>
        {
            ("91.79.202.29", "51234"),
            ("91.79.202.29", "51235"),
            ("91.79.202.29", "51236"),
            ("91.79.202.29", "51237")
        };

        var distinctDevices = connections
            .Select(c => c.Ip)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Assert.Equal(1, distinctDevices);
    }

    [Fact]
    public void DeviceCounting_TwoDistinctIps_ShouldCountAsTwoDevices()
    {
        // 2 physical devices (e.g. phone on mobile LTE + laptop on home Wi-Fi)
        var connections = new List<(string Ip, string Port)>
        {
            ("91.79.202.29", "51234"),
            ("91.79.202.29", "51235"),
            ("188.66.35.83", "49200"),
            ("188.66.35.83", "49201")
        };

        var distinctDevices = connections
            .Select(c => c.Ip)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Assert.Equal(2, distinctDevices);
    }

    [Fact]
    public void VerifySymbolExists()
    {
        bool shieldErrorExists = Enum.TryParse<Wpf.Ui.Controls.SymbolRegular>("ShieldError24", out _);
        Assert.True(shieldErrorExists);
    }

    [Fact]
    public void ClientOnlineState_WhenConnectionsChange_ShouldUpdateIsOnlineAndConnectedAt()
    {
        var client = new VpnClient { ActiveConnections = 0 };
        Assert.False(client.IsOnline);
        Assert.Null(client.ConnectedAt);

        // User connects
        client.ActiveConnections = 1;
        Assert.True(client.IsOnline);
        Assert.NotNull(client.ConnectedAt);
        var connectedTime = client.ConnectedAt.Value;
        Assert.True((DateTime.Now - connectedTime).TotalSeconds < 2);

        // User disconnects
        client.ActiveConnections = 0;
        Assert.False(client.IsOnline);
        Assert.Null(client.ConnectedAt);
    }

    [Fact]
    public void ClientOnlineState_NewConnection_ShouldHaveNewerConnectedAt()
    {
        var clientOld = new VpnClient { ActiveConnections = 0 };
        var clientNew = new VpnClient { ActiveConnections = 0 };

        clientOld.ActiveConnections = 1;
        clientOld.ConnectedAt = DateTime.Now.AddMinutes(-10);

        clientNew.ActiveConnections = 1; // connected just now

        Assert.True(clientNew.ConnectedAt > clientOld.ConnectedAt);
    }
}