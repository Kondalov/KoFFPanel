using KoFFPanel.Infrastructure.Services;
using System;
using Xunit;

namespace KoFFPanel.Tests;

public class SshGuardTests
{
    [Theory]
    [InlineData("admin")]
    [InlineData("user@example.com")]
    [InlineData("user_name")]
    [InlineData("user-name")]
    [InlineData("user.name")]
    [InlineData("client123@domain.co.uk")]
    public void IsValidEmail_WithValidInputs_ShouldReturnTrue(string input)
    {
        Assert.True(SshGuard.IsValidEmail(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("user;rm -rf /")]
    [InlineData("user && whoami")]
    [InlineData("user|cat /etc/passwd")]
    [InlineData("user`id`")]
    [InlineData("user$(whoami)")]
    [InlineData("user\nnewline")]
    [InlineData("user>output.txt")]
    public void IsValidEmail_WithMaliciousOrInvalidInputs_ShouldReturnFalse(string? input)
    {
        Assert.False(SshGuard.IsValidEmail(input));
    }

    [Theory]
    [InlineData("e7b686d0-40e9-4e71-92be-6cf7b4478144")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("E7B686D0-40E9-4E71-92BE-6CF7B4478144")]
    public void IsValidUuid_WithValidUuids_ShouldReturnTrue(string uuid)
    {
        Assert.True(SshGuard.IsValidUuid(uuid));
    }

    [Theory]
    [InlineData("not-a-uuid")]
    [InlineData("e7b686d040e94e7192be6cf7b4478144")]
    [InlineData("e7b686d0-40e9-4e71-92be-6cf7b4478144;whoami")]
    [InlineData("e7b686d0-40e9-4e71-92be-6cf7b4478144'")]
    [InlineData(null)]
    [InlineData("")]
    public void IsValidUuid_WithInvalidUuids_ShouldReturnFalse(string? uuid)
    {
        Assert.False(SshGuard.IsValidUuid(uuid));
    }

    [Fact]
    public void Escape_ShouldWrapInSingleQuotesAndEscapeInnerSingleQuotes()
    {
        Assert.Equal("''", SshGuard.Escape(null));
        Assert.Equal("''", SshGuard.Escape(""));
        Assert.Equal("'hello'", SshGuard.Escape("hello"));
        Assert.Equal("'hello'\\''world'", SshGuard.Escape("hello'world"));
        Assert.Equal("'; rm -rf / ;'", SshGuard.Escape("; rm -rf / ;"));
    }

    [Fact]
    public void ThrowIfInvalid_WithInjection_ShouldThrowArgumentException()
    {
        Assert.Throws<ArgumentException>(() => SshGuard.ThrowIfInvalid("admin; cat /etc/shadow", null));
        Assert.Throws<ArgumentException>(() => SshGuard.ThrowIfInvalid("admin", "invalid-uuid"));
        Assert.Null(Record.Exception(() => SshGuard.ThrowIfInvalid("valid.user@mail.com", "e7b686d0-40e9-4e71-92be-6cf7b4478144")));
    }
}
