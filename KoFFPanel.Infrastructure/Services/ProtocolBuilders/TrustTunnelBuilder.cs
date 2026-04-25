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
    public int DefaultPort => 5443;

    public async Task<ServerInbound> GenerateNewInboundAsync(ISshService ssh, int port)
    {
        string certDir = "/opt/trusttunnel2/certs";
        string certPath = $"{certDir}/server.crt";
        string keyPath = $"{certDir}/server.key";
        string hostname = "vpn.endpoint"; // Более реалистичный SNI для обхода DPI

        await ssh.ExecuteCommandAsync($"mkdir -p {certDir}");
        // Генерация сертификата с корректным SAN для SNI
        string certCmd = $"openssl req -x509 -nodes -newkey rsa:2048 -keyout {keyPath} -out {certPath} -days 3650 -subj \"/CN={hostname}\" -addext \"subjectAltName = DNS:{hostname}\" 2>/dev/null";
        await ssh.ExecuteCommandAsync(certCmd);

        var settings = new
        {
            certPath = certPath,
            keyPath = keyPath,
            sni = hostname,
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
        string hostname = settings.GetProperty("sni").GetString() ?? "vpn.trusttunnel.local";
        string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;

        // Генерируем идеальный рабочий клиентский TOML-конфиг
        return $@"loglevel = ""info""
vpn_mode = ""general""
killswitch_enabled = true
post_quantum_group_enabled = true
exclusions = []

[endpoint]
hostname = ""{hostname}""
addresses = [""{safeIp}:{inbound.Port}""]
has_ipv6 = true
username = ""{clientEmail}""
password = ""{clientUuid}""
client_random = """"
skip_verification = true
upstream_protocol = ""http3""
anti_dpi = true
dns_upstreams = [""tls://1.1.1.1"", ""tls://8.8.8.8""]

[listener.tun]
included_routes = [""0.0.0.0/0"", ""2000::/3""]
excluded_routes = [""10.0.0.0/8"", ""172.16.0.0/12"", ""192.168.0.0/16"", ""fc00::/7""]
mtu_size = 1280
change_system_dns = true";
    }
}