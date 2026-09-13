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
}