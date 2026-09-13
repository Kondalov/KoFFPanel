using KoFFPanel.Application.Interfaces;
using KoFFPanel.Infrastructure.Services;
using System.Threading.Tasks;
using Xunit;

namespace KoFFPanel.Tests;

public class RealitySniScannerTests
{
    private class FakeLogger : IAppLogger
    {
        public void Log(string module, string message) { }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task CheckSniAsync_WithInvalidOrEmptySni_ShouldReturnFalse(string? sni)
    {
        var scanner = new RealitySniScannerService(new FakeLogger(), null!, null!);
        var (isAccessible, _, error) = await scanner.CheckSniAsync(sni!);

        Assert.False(isAccessible);
        Assert.Equal("SNI пуст", error);
    }

    [Fact]
    public void DefaultSniPool_ShouldContainTrustedCdnDomains()
    {
        Assert.Contains("dl.google.com", RealitySniScannerService.DefaultSniPool);
        Assert.Contains("www.microsoft.com", RealitySniScannerService.DefaultSniPool);
        Assert.Contains("speed.cloudflare.com", RealitySniScannerService.DefaultSniPool);
    }
}
