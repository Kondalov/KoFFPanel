namespace KoFFPanel.Domain.Entities;

public class VpnProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int Port { get; set; } = 22;
    public string Username { get; set; } = "root";
    public string Password { get; set; } = "";
    public string? KeyPath { get; set; }

    // ПОЛЯ ДЛЯ REALITY (Добавляем это!)
    public int VpnPort { get; set; } = 443;
    public string Uuid { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string Sni { get; set; } = "www.microsoft.com";
}