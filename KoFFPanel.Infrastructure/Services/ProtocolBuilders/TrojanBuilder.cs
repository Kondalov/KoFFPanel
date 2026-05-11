using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Domain.Entities;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services.ProtocolBuilders;

public partial class TrojanBuilder : IProtocolBuilder
{
    public string ProtocolType => "trojan";
    public string DisplayName => "Trojan (TLS)";
    public string TransportType => "tcp";
    public int DefaultPort => 4434;

    public async Task<ServerInbound> GenerateNewInboundAsync(ISshService ssh, int port)
    {
        string certDir = $"/etc/koff/trojan_{port}";
        string certPath = $"{certDir}/cert.crt";
        string keyPath = $"{certDir}/private.key";
        string sni = "bing.com";

        // Умный алгоритм подготовки директорий и генерации самоподписанного сертификата
        await ssh.ExecuteCommandAsync($"mkdir -p {certDir}");
        string certCmd = $"openssl req -x509 -nodes -newkey rsa:2048 -keyout {keyPath} -out {certPath} -days 3650 -subj \"/CN={sni}\" 2>/dev/null";
        await ssh.ExecuteCommandAsync(certCmd);

        var settings = new
        {
            certPath = certPath,
            keyPath = keyPath,
            sni = sni
        };

        return new ServerInbound
        {
            Tag = $"trojan-{port}",
            Protocol = ProtocolType,
            Port = port,
            SettingsJson = JsonSerializer.Serialize(settings)
        };
    }

    public string GenerateClientLink(ServerInbound inbound, string serverIp, string clientUuid, string clientEmail)
    {
        var settings = JsonDocument.Parse(inbound.SettingsJson).RootElement;
        string sni = settings.GetProperty("sni").GetString() ?? "bing.com";
        string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
        string encodedName = Uri.EscapeDataString($"KoFFPanel-{clientEmail}");

        return $"trojan://{clientUuid}@{safeIp}:{inbound.Port}?security=tls&sni={sni}&type=tcp&allowInsecure=1#{encodedName}";
    }
}
