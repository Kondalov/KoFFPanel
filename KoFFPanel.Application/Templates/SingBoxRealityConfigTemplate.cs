using System;

namespace KoFFPanel.Application.Templates;

public static class SingBoxRealityConfigTemplate
{
    public static string GenerateServerConfig(int port, string uuid, string sni, string privateKey, string shortId)
    {
        return $$"""
        {
          "log": {
            "level": "info",
            "timestamp": true
          },
          "inbounds": [
            {
              "type": "vless",
              "tag": "vless-in",
              "listen": "::",
              "listen_port": {{port}},
              "users": [
                {
                  "name": "Admin",
                  "uuid": "{{uuid}}",
                  "flow": "xtls-rprx-vision"
                }
              ],
              "tls": {
                "enabled": true,
                "server_name": "{{sni}}",
                "reality": {
                  "enabled": true,
                  "handshake": {
                    "server": "{{sni}}",
                    "server_port": 443
                  },
                  "private_key": "{{privateKey}}",
                  "short_id": [
                    "{{shortId}}"
                  ]
                }
              }
            }
          ],
          "outbounds": [
            {
              "type": "direct",
              "tag": "direct"
            },
            {
              "type": "block",
              "tag": "block"
            }
          ],
          "route": {
            "rules": [
              {
                "inbound": "vless-in",
                "action": "sniff"
              },
              {
                "protocol": "bittorrent",
                "outbound": "block"
              },
              {
                "domain_keyword": [
                  "torrent",
                  "tracker",
                  "rutracker",
                  "nnmclub",
                  "kinozal",
                  "rutor",
                  "piratebay",
                  "tapochek",
                  "lostfilm"
                ],
                "outbound": "block"
              }
            ],
            "final": "direct",
            "auto_detect_interface": true
          }
        }
        """;
    }

    public static string GenerateClientConfig(string ip, int port, string uuid, string sni, string pubKey, string shortId)
    {
        return $$"""
        {
          "outbounds": [
            {
              "type": "vless",
              "tag": "PROXY-SingBox",
              "server": "{{ip}}",
              "server_port": {{port}},
              "uuid": "{{uuid}}",
              "flow": "xtls-rprx-vision",
              "tls": {
                "enabled": true,
                "server_name": "{{sni}}",
                "utls": {
                  "enabled": true,
                  "fingerprint": "chrome"
                },
                "reality": {
                  "enabled": true,
                  "public_key": "{{pubKey}}",
                  "short_id": "{{shortId}}"
                }
              },
              "packet_encoding": "xudp"
            }
          ]
        }
        """;
    }
}