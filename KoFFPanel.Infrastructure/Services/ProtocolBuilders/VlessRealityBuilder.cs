using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Domain.Entities;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services.ProtocolBuilders;

public class VlessRealityBuilder : IProtocolBuilder
{
    public string ProtocolType => "vless";
    public string DisplayName => "VLESS (XTLS-Reality)";
    public string TransportType => "tcp";
    public int DefaultPort => 443;

    public async Task<ServerInbound> GenerateNewInboundAsync(ISshService ssh, int port)
    {
        string keyCmd = "if command -v sing-box >/dev/null 2>&1; then sing-box generate reality-keypair; else /usr/local/bin/xray x25519; fi";
        string keypairOutput = await ssh.ExecuteCommandAsync(keyCmd);

        string privateKey = "";
        string publicKey = "";

        var lines = keypairOutput.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        foreach (var line in lines)
        {
            if (line.StartsWith("Private", StringComparison.OrdinalIgnoreCase))
                privateKey = line.Split(':')[1].Trim();

            if (line.StartsWith("Public", StringComparison.OrdinalIgnoreCase))
                publicKey = line.Split(':')[1].Trim();
        }

        string shortId = Guid.NewGuid().ToString("N").Substring(0, 16);

        var settings = new
        {
            privateKey = privateKey,
            publicKey = publicKey,
            shortId = shortId,
            sni = "google.com"
        };

        return new ServerInbound
        {
            Tag = $"vless-reality-{port}",
            Protocol = ProtocolType,
            Port = port,
            SettingsJson = JsonSerializer.Serialize(settings)
        };
    }

    public string GenerateClientLink(ServerInbound inbound, string serverIp, string clientUuid, string clientEmail)
    {
        var settings = JsonDocument.Parse(inbound.SettingsJson).RootElement;
        string pubKey = settings.GetProperty("publicKey").GetString() ?? "";
        string sni = settings.GetProperty("sni").GetString() ?? "google.com";
        string shortId = settings.GetProperty("shortId").GetString() ?? "";

        string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;

        // ИСПРАВЛЕНИЕ: Добавлен alpn=h2 для Hiddify 4.1.1
        return $"vless://{clientUuid}@{safeIp}:{inbound.Port}?type=tcp&security=reality&pbk={pubKey}&fp=chrome&sni={sni}&sid={shortId}&spx=%2F&flow=xtls-rprx-vision&alpn=h2#KoFFPanel-{clientEmail}";
    }
}