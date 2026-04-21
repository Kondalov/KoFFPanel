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

    // ССЫЛКА ПОДПИСКИ (Использует динамический порт)
    public string HttpLink => $"http://{IpAddress}:{Port}/{Uuid}";

    // VLESS ССЫЛКА
    public string VlessLink => $"vless://{Uuid}@{IpAddress}:{Port}?type=tcp&security=reality&pbk={PublicKey}&fp=chrome&sni={Sni}&sid={ShortId}&spx=%2F&flow=xtls-rprx-vision#KoFFPanel_{IpAddress}";

    // JSON КЛИЕНТА
    public string ClientJson => $$"""
    {
      "outbounds": [
        {
          "protocol": "vless",
          "settings": {
            "vnext": [
              {
                "address": "{{IpAddress}}",
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
