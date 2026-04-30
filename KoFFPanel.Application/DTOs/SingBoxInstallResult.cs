using KoFFPanel.Application.Templates;

namespace KoFFPanel.Application.DTOs;

public class SingBoxInstallResult
{
    public string Uuid { get; set; } = "";
    public string PrivateKey { get; set; } = "";
    public string PublicKey { get; set; } = "";
    public string ShortId { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public int Port { get; set; }
    public string Sni { get; set; } = "";

    // ИСПРАВЛЕНИЕ: Поддержка кастомного домена
    public string? CustomDomain { get; set; }

    public string HttpLink
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(CustomDomain))
            {
                string domain = CustomDomain.Trim().TrimEnd('/');
                if (!domain.StartsWith("http")) domain = "https://" + domain;
                return $"{domain}/{Uuid}";
            }
            return $"http://{IpAddress}:8080/{Uuid}";
        }
    }

    public string VlessLink => $"vless://{Uuid}@{IpAddress}:{Port}?type=tcp&security=reality&pbk={PublicKey}&fp=chrome&sni={Sni}&sid={ShortId}&spx=%2F&flow=xtls-rprx-vision&alpn=h2#SingBox_{IpAddress}";
    public string ClientJson => SingBoxRealityConfigTemplate.GenerateClientConfig(IpAddress, Port, Uuid, Sni, PublicKey, ShortId);
}
