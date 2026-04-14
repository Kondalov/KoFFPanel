using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Domain.Entities;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services.ProtocolBuilders;

public class Hysteria2Builder : IProtocolBuilder
{
    public string ProtocolType => "hysteria2";
    public string DisplayName => "Hysteria 2 (UDP-Bruteforce)";
    public string TransportType => "udp";
    public int DefaultPort => 8443;

    public async Task<ServerInbound> GenerateNewInboundAsync(ISshService ssh, int port)
    {
        // Магия генерации самоподписанного TLS-сертификата (нужен для Hy2)
        string certPath = $"/etc/sing-box/hy2_{port}.crt";
        string keyPath = $"/etc/sing-box/hy2_{port}.key";
        string certCmd = $"openssl req -x509 -nodes -newkey rsa:2048 -keyout {keyPath} -out {certPath} -days 3650 -subj \"/CN=bing.com\" 2>/dev/null";
        await ssh.ExecuteCommandAsync(certCmd);

        string obfsPassword = Guid.NewGuid().ToString("N").Substring(0, 10);

        var settings = new
        {
            certPath = certPath,
            keyPath = keyPath,
            obfsPassword = obfsPassword,
            sni = "bing.com"
        };

        return new ServerInbound
        {
            Tag = $"hysteria2-{port}",
            Protocol = ProtocolType,
            Port = port,
            SettingsJson = JsonSerializer.Serialize(settings)
        };
    }

    public string GenerateClientLink(ServerInbound inbound, string serverIp, string clientUuid, string clientEmail)
    {
        var settings = JsonDocument.Parse(inbound.SettingsJson).RootElement;
        string sni = settings.GetProperty("sni").GetString() ?? "bing.com";
        string obfs = settings.GetProperty("obfsPassword").GetString() ?? "";

        string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
        return $"hy2://{clientUuid}@{safeIp}:{inbound.Port}?sni={sni}&insecure=1&obfs=salamander&obfs-password={obfs}#KoFFPanel-{clientEmail}";
    }
}