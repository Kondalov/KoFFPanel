using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Services;
using System.Collections.Generic;
using System.Threading.Tasks;
using Xunit;

namespace KoFFPanel.Tests;

public class SmartPortValidatorTests
{
    private class FakeProfileRepository : IProfileRepository
    {
        public List<VpnProfile> Profiles { get; set; } = new();
        public List<VpnProfile> LoadProfiles() => Profiles;
        public void SaveProfiles(List<VpnProfile> profiles) => Profiles = profiles;
        public void AddProfile(VpnProfile profile) => Profiles.Add(profile);
        public void UpdateProfile(VpnProfile updatedProfile) { }
        public void DeleteProfile(string id) { }
    }

    private class FakeAppLogger : IAppLogger
    {
        public void Log(string module, string message) { }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(100000)]
    public async Task ValidatePortAsync_OutOfRangePorts_ShouldReturnFalse(int port)
    {
        var validator = new SmartPortValidator(new FakeProfileRepository(), new FakeAppLogger());
        var (isValid, errorMsg) = await validator.ValidatePortAsync(null!, "server1", port, "vless");

        Assert.False(isValid);
        Assert.Contains("от 1 до 65535", errorMsg);
    }

    [Theory]
    [InlineData(22)]
    [InlineData(53)]
    [InlineData(80)]
    public async Task ValidatePortAsync_ReservedSystemPorts_ShouldReturnFalse(int port)
    {
        var validator = new SmartPortValidator(new FakeProfileRepository(), new FakeAppLogger());
        var (isValid, errorMsg) = await validator.ValidatePortAsync(null!, "server1", port, "vless");

        Assert.False(isValid);
        Assert.Contains("зарезервирован системой", errorMsg);
    }

    [Fact]
    public async Task ValidatePortAsync_ValidCustomPort_WithoutSsh_ShouldReturnTrue()
    {
        var validator = new SmartPortValidator(new FakeProfileRepository(), new FakeAppLogger());
        var (isValid, errorMsg) = await validator.ValidatePortAsync(null!, "server1", 8443, "vless");

        Assert.True(isValid);
        Assert.Equal("Порт свободен", errorMsg);
    }
}
