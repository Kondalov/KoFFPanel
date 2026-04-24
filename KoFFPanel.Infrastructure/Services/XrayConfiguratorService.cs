using KoFFPanel.Application.Interfaces;
using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Text.Json.Nodes;

namespace KoFFPanel.Infrastructure.Services;

public class XrayConfiguratorService : IXrayConfiguratorService
{
    private readonly IAppLogger _logger;

    public XrayConfiguratorService(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<(bool IsSuccess, string Message, string VlessLink)> InitializeRealityAsync(ISshService ssh, string serverIp)
    {
        try
        {
            if (!ssh.IsConnected) return (false, "Нет подключения по SSH", "");

            _logger.Log("CONFIG", "Генерация X25519 ключей...");
            var keysOutput = await ssh.ExecuteCommandAsync("/usr/local/bin/xray x25519");

            var privMatch = Regex.Match(keysOutput, @"(?i)(?:Private\s*key|PrivateKey)\s*:\s*(\S+)");
            var pubMatch = Regex.Match(keysOutput, @"(?i)(?:Public\s*key|PublicKey|Password\s*\(PublicKey\))\s*:\s*(\S+)");

            if (!privMatch.Success || !pubMatch.Success)
            {
                _logger.Log("CONFIG-ERROR", $"Не удалось распарсить ключи. Вывод ядра: {keysOutput}");
                return (false, "ОШИБКА: Ядро вернуло неверный формат ключей.", "");
            }

            string privKey = privMatch.Groups[1].Value.Trim();
            string pubKey = pubMatch.Groups[1].Value.Trim();
            string uuid = Guid.NewGuid().ToString();
            string shortId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLower();

            // ИСПРАВЛЕНИЕ: Смена SNI. dl.google.com практически никогда не блокируется DPI.
            string sni = "dl.google.com";
            string encodedName = Uri.EscapeDataString($"KoFFPanel_{serverIp}");

            string configJson = $$"""
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
                  "port": 443,
                  "protocol": "vless",
                  "settings": {
                    "clients": [
                      { "id": "{{uuid}}", "flow": "xtls-rprx-vision", "email": "Admin" }
                    ],
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
                      "privateKey": "{{privKey}}",
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
                  "settings": { 
                    "address": "127.0.0.1",
                    "network": "tcp"
                  },
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
                  { "type": "field", "protocol": ["bittorrent"], "outboundTag": "torrent-logger" },
                  { 
                    "type": "field", 
                    "domain": [
                      "domain:nnmclub.to",
                      "domain:rutracker.org",
                      "domain:rutor.info",
                      "domain:kinozal.tv",
                      "domain:tapochek.net",
                      "keyword:torrent"
                    ], 
                    "outboundTag": "torrent-logger" 
                  }
                ]
              }
            }
            """;

            string base64Json = Convert.ToBase64String(Encoding.UTF8.GetBytes(configJson.Replace("\r", "")));
            await ssh.ExecuteCommandAsync($"echo '{base64Json}' | base64 -d > /tmp/config_test.json");

            var testResult = await ssh.ExecuteCommandAsync("/usr/local/bin/xray run -test -config /tmp/config_test.json");

            if (!testResult.Contains("Configuration OK"))
            {
                return (false, "ОШИБКА: Ядро отклонило сгенерированный конфиг!", "");
            }

            await ssh.ExecuteCommandAsync("mkdir -p /var/log/xray");
            await ssh.ExecuteCommandAsync("touch /var/log/xray/access.log /var/log/xray/error.log");
            await ssh.ExecuteCommandAsync("chmod -R 777 /var/log/xray");

            string logrotateCmd = @"cat << 'EOF' > /etc/logrotate.d/xray
/var/log/xray/*.log {
    daily
    rotate 3
    missingok
    notifempty
    compress
    delaycompress
    copytruncate
}
EOF";
            await ssh.ExecuteCommandAsync(logrotateCmd);

            await ssh.ExecuteCommandAsync("mv /tmp/config_test.json /usr/local/etc/xray/config.json");
            await ssh.ExecuteCommandAsync("systemctl restart xray");

            // ИСПРАВЛЕНИЕ: Добавлен обязательный ALPN для современных клиентов.
            string vlessLink = $"vless://{uuid}@{serverIp}:443?security=reality&encryption=none&alpn=h2,http/1.1&pbk={pubKey}&headerType=none&fp=chrome&type=tcp&flow=xtls-rprx-vision&sni={sni}&sid={shortId}#{encodedName}";

            return (true, "VLESS-Reality настроен!", vlessLink);
        }
        catch (Exception ex)
        {
            return (false, $"КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}", "");
        }
    }

    public async Task<(bool IsSuccess, string Message)> UpdateGeoDataAsync(ISshService ssh)
    {
        if (!ssh.IsConnected) return (false, "Нет подключения");

        try
        {
            _logger.Log("GEO", "Запуск обновления базы GeoSite (Защита от торрент-трекеров)...");

            string script = @"
mkdir -p /usr/local/share/xray
rm -f /tmp/geosite.dat

URLS=(
    'https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat'
    'https://mirror.ghproxy.com/https://github.com/Loyalsoldier/v2ray-rules-dat/releases/latest/download/geosite.dat'
    'https://fastly.jsdelivr.net/gh/Loyalsoldier/v2ray-rules-dat@release/geosite.dat'
    'https://raw.githubusercontent.com/Loyalsoldier/v2ray-rules-dat/release/geosite.dat'
)

for url in ""${URLS[@]}""; do
    if command -v curl >/dev/null 2>&1; then
        curl -sL -A 'Mozilla/5.0' --connect-timeout 10 -o /tmp/geosite.dat ""$url""
    else
        wget -qU 'Mozilla/5.0' -O /tmp/geosite.dat --timeout=10 ""$url""
    fi
    
    if [ -f /tmp/geosite.dat ]; then
        SIZE=$(wc -c < /tmp/geosite.dat | tr -d ' ')
        if [ ""$SIZE"" -gt 1000000 ]; then
            mv /tmp/geosite.dat /usr/local/share/xray/geosite.dat
            cp /usr/local/share/xray/geosite.dat /usr/local/bin/geosite.dat 2>/dev/null
            echo 'SUCCESS'
            exit 0
        fi
        rm -f /tmp/geosite.dat
    fi
done

echo 'ERROR_ALL_FAILED'
";
            string safeScriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Replace("\r", "")));
            string result = await ssh.ExecuteCommandAsync($"echo '{safeScriptBase64}' | base64 -d | bash");

            if (result.Contains("SUCCESS"))
            {
                // ИСПРАВЛЕНИЕ: База скачана. Теперь C# элегантно обновляет JSON и добавляет мощные правила GeoSite!
                string rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null");
                if (!string.IsNullOrWhiteSpace(rawJson) && rawJson.Contains("{"))
                {
                    try
                    {
                        var root = JsonNode.Parse(rawJson);
                        var rules = root?["routing"]?["rules"]?.AsArray();
                        if (rules != null)
                        {
                            bool configChanged = false;
                            foreach (var rule in rules)
                            {
                                if (rule?["outboundTag"]?.ToString() == "torrent-logger" && rule["domain"] != null)
                                {
                                    var domains = rule["domain"].AsArray();
                                    bool hasGeosite = false;
                                    foreach (var d in domains)
                                    {
                                        if (d?.ToString() == "geosite:category-torrent") hasGeosite = true;
                                    }
                                    if (!hasGeosite)
                                    {
                                        domains.Add("geosite:category-torrent");
                                        configChanged = true;
                                    }
                                }
                            }

                            if (configChanged)
                            {
                                string updatedJson = root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                                string base64Config = Convert.ToBase64String(Encoding.UTF8.GetBytes(updatedJson.Replace("\r", "")));
                                await ssh.ExecuteCommandAsync($"echo '{base64Config}' | base64 -d > /tmp/config_geo.json");

                                var test = await ssh.ExecuteCommandAsync("/usr/local/bin/xray run -test -config /tmp/config_geo.json");
                                if (test.Contains("Configuration OK"))
                                {
                                    await ssh.ExecuteCommandAsync("mv /tmp/config_geo.json /usr/local/etc/xray/config.json");
                                }
                            }
                        }
                    }
                    catch { _logger.Log("GEO", "Ошибка парсинга JSON, но база загружена."); }
                }

                await ssh.ExecuteCommandAsync("systemctl restart xray");
                return (true, "Базы успешно обновлены и применены!");
            }
            else
            {
                _logger.Log("GEO-ERROR", $"Сбой скачивания. Ответ сервера: {result}");
                return (false, "Сбой скачивания баз (серверы недоступны).");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка обновления: {ex.Message}");
        }
    }
}