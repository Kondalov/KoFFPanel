using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace KoFFPanel.Application.Templates;

public static class SingBoxRealityConfigTemplate
{
    // ИСПРАВЛЕНИЕ: Делаем новые параметры опциональными (null по умолчанию). 
    // Это сохраняет обратную совместимость для кода установки сервера!
    public static string GenerateServerConfig(
        int port,
        string uuid,
        string sni,
        string privateKey,
        string shortId,
        List<string>? p2pBlockedUsers = null,
        List<string>? torrentDomains = null)
    {
        // Защита от NullReference
        p2pBlockedUsers ??= new List<string>();

        // Если список доменов пуст, кладем дефолтные
        if (torrentDomains == null || !torrentDomains.Any())
        {
            torrentDomains = new List<string> { "torrent", "tracker", "rutracker", "nnmclub", "kinozal", "rutor", "piratebay", "tapochek", "lostfilm" };
        }

        // Безопасная сериализация массивов в JSON
        string blockedUsersJson = JsonSerializer.Serialize(p2pBlockedUsers);
        string torrentDomainsJson = JsonSerializer.Serialize(torrentDomains);

        return $$"""
        {
          "log": {
            "level": "info",
            "timestamp": true
          },
          "dns": {
            "servers": [
              {
                "type": "https",
                "tag": "remote",
                "server": "8.8.8.8",
                "domain_resolver": "local"
              },
              {
                "type": "udp",
                "tag": "local",
                "server": "8.8.8.8"
              }
            ],
            "rules": [
              {
                "domain_suffix": [
                  "openai.com",
                  "chatgpt.com",
                  "auth0.com",
                  "google.com",
                  "gemini.google.com",
                  "googleapis.com",
                  "generativelanguage.googleapis.com",
                  "claude.ai",
                  "anthropic.com"
                ],
                "server": "remote"
              }
            ],
            "final": "remote",
            "strategy": "prefer_ipv4"
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
            "default_domain_resolver": "local",
            "rules": [
              {
                "action": "sniff"
              },
              {
                "domain_suffix": [
                  "openai.com",
                  "chatgpt.com",
                  "auth0.com",
                  "google.com",
                  "gemini.google.com",
                  "googleapis.com",
                  "generativelanguage.googleapis.com",
                  "claude.ai",
                  "anthropic.com"
                ],
                "outbound": "direct"
              },
              {
                "user": {{blockedUsersJson}},
                "protocol": "bittorrent",
                "outbound": "block"
              },
              {
                "user": {{blockedUsersJson}},
                "domain_keyword": {{torrentDomainsJson}},
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