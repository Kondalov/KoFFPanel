namespace KoFFPanel.Application.Templates;

public static class XrayRealityConfigTemplate
{
    public static string Generate(string ipProtocol, int port, string uuid, string sni, string privateKey, string shortId)
    {
        string listenIp = ipProtocol switch
        {
            "IPv6" => "::",
            "Dual Stack" => "::",
            _ => "0.0.0.0"
        };

        // ИСПОЛЬЗУЕТСЯ ТВОЙ ЭТАЛОННЫЙ КОНФИГ
        return $$"""
        {
          "log": { "loglevel": "warning" },
          "inbounds": [
            {
              "port": {{port}}, 
              "listen": "{{listenIp}}", 
              "protocol": "vless",
              "settings": { 
                "clients": [{ "id": "{{uuid}}", "flow": "xtls-rprx-vision" }], 
                "decryption": "none" 
              },
              "streamSettings": { 
                "network": "tcp", 
                "security": "reality", 
                "realitySettings": { 
                  "show": false, 
                  "dest": "{{sni}}:443", 
                  "xver": 0, 
                  "serverNames": ["{{sni}}"], 
                  "privateKey": "{{privateKey}}", 
                  "shortIds": ["{{shortId}}"], 
                  "fingerprint": "chrome", 
                  "spiderX": "/" 
                } 
              }
            }
          ],
          "outbounds": [
            { "tag": "DIRECT", "protocol": "freedom" },
            { "tag": "BLOCK", "protocol": "blackhole" }
          ]
        }
        """;
    }
}