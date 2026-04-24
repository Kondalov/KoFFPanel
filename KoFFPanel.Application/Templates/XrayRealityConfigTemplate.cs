using System;

namespace KoFFPanel.Application.Templates;

public static class XrayRealityConfigTemplate
{
    public static string Generate(string ipVersion, int port, string uuid, string sni, string privateKey, string shortId)
    {
        return $$"""
        {
          "log": {
            "access": "/var/log/xray/access.log",
            "error": "/var/log/xray/error.log",
            "loglevel": "warning"
          },
          "stats": {},
          "api": {
            "tag": "api",
            "services": ["StatsService"]
          },
          "policy": {
            "levels": {
              "0": { "statsUserUplink": true, "statsUserDownlink": true }
            },
            "system": {
              "statsInboundUplink": true, "statsInboundDownlink": true,
              "statsOutboundUplink": true, "statsOutboundDownlink": true
            }
          },
          "inbounds": [
            {
              "port": {{port}},
              "protocol": "vless",
              "settings": {
                "clients": [],
                "decryption": "none"
              },
              "streamSettings": {
                "network": "tcp",
                "security": "reality",
                "realitySettings": {
                  "show": false,
                  "dest": "google.com:443",
                  "xver": 0,
                  "serverNames": ["google.com"],
                  "privateKey": "{{privateKey}}",
                  "shortIds": ["{{shortId}}"]
                }
              },
              "sniffing": {
                "enabled": true,
                "destOverride": ["http", "tls", "quic"],
                "routeOnly": true
              }
            },
            {
              "listen": "127.0.0.1",
              "port": 10085,
              "protocol": "dokodemo-door",
              "settings": { "address": "127.0.0.1" },
              "tag": "api"
            }
          ],
          "outbounds": [
            { "protocol": "freedom", "tag": "direct" },
            { "protocol": "freedom", "tag": "torrent-logger" },
            { "protocol": "blackhole", "tag": "block" }
          ],
          "routing": {
            "domainStrategy": "AsIs",
            "rules": [
              { "inboundTag": ["api"], "outboundTag": "api", "type": "field" },
              { "type": "field", "protocol": ["bittorrent"], "outboundTag": "torrent-logger" }
            ]
          }
        }
        """;
    }
}