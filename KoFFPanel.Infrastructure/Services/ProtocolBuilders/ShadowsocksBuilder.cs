using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Domain.Entities;
using System;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services.ProtocolBuilders;

public partial class ShadowsocksBuilder : IProtocolBuilder
{
    public string ProtocolType => "shadowsocks";
    public string DisplayName => "Shadowsocks (AES-256-GCM)";
    public string TransportType => "tcp";
    public int DefaultPort => 8388;

    public Task<ServerInbound> GenerateNewInboundAsync(ISshService ssh, int port)
    {
        // SS не требует сертификатов. Вся магия генерации происходит локально для 100% надежности.
        var settings = new
        {
            method = "aes-256-gcm"
        };

        return Task.FromResult(new ServerInbound
        {
            Tag = $"shadowsocks-{port}",
            Protocol = ProtocolType,
            Port = port,
            SettingsJson = JsonSerializer.Serialize(settings)
        });
    }

    public string GenerateClientLink(ServerInbound inbound, string serverIp, string clientUuid, string clientEmail)
    {
        var settings = JsonDocument.Parse(inbound.SettingsJson).RootElement;
        string method = settings.GetProperty("method").GetString() ?? "aes-256-gcm";

        string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
        string encodedName = Uri.EscapeDataString($"KoFFPanel-{clientEmail}");

        string credentials = $"{method}:{clientUuid}";

        // ИСПРАВЛЕНИЕ: Используем стандартный Base64 для SIP002 (без замены + на - и / на _)
        // Некоторые клиенты (Hiddify) не понимают Base64URL в userinfo.
        string base64Creds = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));

        return $"ss://{base64Creds}@{safeIp}:{inbound.Port}#{encodedName}";
    }
}