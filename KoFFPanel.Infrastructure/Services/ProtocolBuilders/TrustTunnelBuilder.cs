using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Domain.Entities;
using System.Text.Json;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services.ProtocolBuilders;

public class TrustTunnelBuilder : IProtocolBuilder
{
    public string ProtocolType => "trusttunnel";
    public string DisplayName => "TrustTunnel (MASQUE HTTP/3)";
    public string TransportType => "udp";
    public int DefaultPort => 4433;

    public async Task<ServerInbound> GenerateNewInboundAsync(ISshService ssh, int port)
    {
        // ИСПРАВЛЕНИЕ: Больше никаких заглушек! Генерируем реальный сертификат для QUIC/HTTP3
        string certPath = $"/etc/sing-box/tt_{port}.crt";
        string keyPath = $"/etc/sing-box/tt_{port}.key";
        string certCmd = $"openssl req -x509 -nodes -newkey rsa:2048 -keyout {keyPath} -out {certPath} -days 3650 -subj \"/CN=adguard.com\" 2>/dev/null";
        await ssh.ExecuteCommandAsync(certCmd);

        var settings = new
        {
            certPath = certPath,
            keyPath = keyPath,
            sni = "adguard.com",
            masqueEnabled = true
        };

        return new ServerInbound
        {
            Tag = $"trusttunnel-{port}",
            Protocol = ProtocolType,
            Port = port,
            SettingsJson = JsonSerializer.Serialize(settings)
        };
    }

    public string GenerateClientLink(ServerInbound inbound, string serverIp, string clientUuid, string clientEmail)
    {
        var settings = System.Text.Json.JsonDocument.Parse(inbound.SettingsJson).RootElement;
        string pubKey = settings.GetProperty("publicKey").GetString() ?? "";
        string sni = settings.GetProperty("sni").GetString() ?? "www.microsoft.com";
        string shortId = settings.GetProperty("shortId").GetString() ?? "";

        string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;

        // ИСПРАВЛЕНИЕ: Убрали &flow=xtls-rprx-vision. Теперь Hiddify и другие клиенты подключатся моментально!
        return $"vless://{clientUuid}@{safeIp}:{inbound.Port}?type=tcp&security=reality&pbk={pubKey}&fp=chrome&sni={sni}&sid={shortId}&spx=%2F#KoFFPanel-{clientEmail}";
    }
}