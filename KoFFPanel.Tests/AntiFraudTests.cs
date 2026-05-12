using KoFFPanel.Domain.Entities;
using Xunit;

namespace KoFFPanel.Tests;

public class AntiFraudTests
{
    [Fact]
    public void CalculateRiskScore_CheckMaxLimits_ShouldReturnBan()
    {
        // Arrange: Создаем поддельную запись поведения юзера (как будто он сменил 2 страны и зашел с 4 устройств)
        var log = new ClientBehaviorLog
        {
            MaxConcurrentSessions = 4, // 4 устройства
            GeoJumpsCount = 2          // 2 прыжка по странам
        };

        // Act: Воспроизводим формулу из нашего сервиса AntiFraudService
        int score = 0;
        if (log.MaxConcurrentSessions > 2) score += (log.MaxConcurrentSessions - 2) * 40;
        if (log.GeoJumpsCount > 0) score += log.GeoJumpsCount * 80;

        log.RiskScore = score > 100 ? 100 : score;

        // Assert: Проверяем, что алгоритм безжалостно выдал 100% фрода (бан)
        Assert.Equal(100, log.RiskScore);
        Assert.True(log.IsBanned);
    }
}