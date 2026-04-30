namespace KoFFPanel.Application.DTOs;

public class XrayInstallResult
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

    // ИСПРАВЛЕНИЕ: Поддержка кастомного узла для подключения
    public string? ConnectionNode { get; set; }

    public string DisplayServer => !string.IsNullOrWhiteSpace(ConnectionNode) ? ConnectionNode.Trim() : IpAddress;

    // ССЫЛКА ПОДПИСКИ
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

    // VLESS ССЫЛКА
    public string VlessLink => $"vless://{Uuid}@{DisplayServer}:{Port}?type=tcp&security=reality&pbk={PublicKey}&fp=chrome&sni={Sni}&sid={ShortId}&spx=%2F&flow=xtls-rprx-vision&alpn=h2#Xray_{IpAddress}";

    // JSON КЛИЕНТА
    public string ClientJson => $$"""
    {
      "outbounds": [
        {
          "protocol": "vless",
          "settings": {
            "vnext": [
              {
                "address": "{{DisplayServer}}",
                "port": {{Port}},
                "users": [{ "id": "{{Uuid}}", "encryption": "none", "flow": "xtls-rprx-vision" }]
              }
            ]
          },
          "streamSettings": {
            "network": "tcp",
            "security": "reality",
            "realitySettings": {
              "fingerprint": "chrome",
              "serverName": "{{Sni}}",
              "publicKey": "{{PublicKey}}",
              "shortId": "{{ShortId}}",
              "spiderX": "/"
            }
          }
        }
      ]
    }
    """;
}
