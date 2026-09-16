using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Domain.Entities;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services.ProtocolBuilders;

public class TuicBuilder : IProtocolBuilder
{
    public string ProtocolType => "tuic";
    public string DisplayName => "TUIC v5 (QUIC/BBR)";
    public string TransportType => "udp";
    public int DefaultPort => 8444;

    public async Task<ServerInbound> GenerateNewInboundAsync(ISshService ssh, int port)
    {
        string certPath = $"/etc/sing-box/tuic_{port}.crt";
        string keyPath = $"/etc/sing-box/tuic_{port}.key";

        await ssh.ExecuteCommandAsync("mkdir -p /etc/sing-box");
        string certCmd = $"if [ ! -f \"{certPath}\" ] || [ ! -f \"{keyPath}\" ]; then openssl ecparam -genkey -name prime256v1 -out \"{keyPath}\" 2>/dev/null && openssl req -new -x509 -days 365 -key \"{keyPath}\" -out \"{certPath}\" -subj \"/CN=bing.com\" 2>/dev/null; fi";
        await ssh.ExecuteCommandAsync(certCmd);

        var settings = new
        {
            certPath = certPath,
            keyPath = keyPath,
            sni = "bing.com",
            congestionControl = "bbr"
        };

        return new ServerInbound
        {
            Tag = $"tuic-{port}",
            Protocol = ProtocolType,
            Port = port,
            SettingsJson = JsonSerializer.Serialize(settings)
        };
    }

    public string GenerateClientLink(ServerInbound inbound, string serverIp, string clientUuid, string clientEmail)
    {
        var settings = JsonDocument.Parse(inbound.SettingsJson).RootElement;
        string sni = settings.TryGetProperty("sni", out var s) ? s.GetString() ?? "bing.com" : "bing.com";

        string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
        string encodedName = Uri.EscapeDataString($"KoFFPanel-{clientEmail}");
        return $"tuic://{clientUuid}:{clientUuid}@{safeIp}:{inbound.Port}?sni={sni}&alpn=h3&congestion_control=bbr&allow_insecure=1&insecure=1#{encodedName}";
    }
}
