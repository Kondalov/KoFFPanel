namespace KoFFPanel.Domain.Entities;

public record VpnProfile
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string IpAddress { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string KeyPath { get; init; } = string.Empty;
}