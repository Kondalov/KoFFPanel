using KoFFPanel.Application.Interfaces;
using System;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class XrayConfiguratorService : IXrayConfiguratorService
{
    private readonly IAppLogger _logger;

    public XrayConfiguratorService(IAppLogger logger) { _logger = logger; }

    public async Task<(bool IsSuccess, string Message, string VlessLink)> InitializeRealityAsync(ISshService ssh, string serverIp)
    {
        try
        {
            if (!ssh.IsConnected) return (false, "Нет подключения по SSH", "");

            _logger.Log("CONFIG", "Умный режим: принудительно убиваем процессы на 443 порту...");
            await ssh.ExecuteCommandAsync("fuser -k 443/tcp 2>/dev/null || true");

            _logger.Log("CONFIG", "Генерация X25519 ключей...");
            var keysOutput = await ssh.ExecuteCommandAsync("/usr/local/bin/xray x25519");
            _logger.Log("CONFIG", "Открываем порт 443 в файрволе...");
            await ssh.ExecuteCommandAsync("ufw allow 443/tcp 2>/dev/null || true");
            await ssh.ExecuteCommandAsync("iptables -I INPUT -p tcp --dport 443 -j ACCEPT 2>/dev/null || true");

            var privMatch = Regex.Match(keysOutput, @"(?i)(?:Private\s*key|PrivateKey)\s*:\s*(\S+)");
            var pubMatch = Regex.Match(keysOutput, @"(?i)(?:Public\s*key|PublicKey|Password\s*\(PublicKey\))\s*:\s*(\S+)");

            if (!privMatch.Success || !pubMatch.Success)
            {
                _logger.Log("CONFIG-ERROR", $"Не удалось распарсить ключи. Вывод ядра: {keysOutput}");
                return (false, "ОШИБКА: Ядро вернуло неверный формат ключей.", "");
            }

            string privKey = privMatch.Groups[1].Value;
            string pubKey = pubMatch.Groups[1].Value;

            string uuid = Guid.NewGuid().ToString();
            string shortId = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLower();
            string sni = "www.microsoft.com";

            // ИСПРАВЛЕНИЕ: Жестко указываем пути к файлам и уровень info
            string configJson = $$"""
            {
              "log": { 
                "access": "/var/log/xray/access.log", 
                "error": "/var/log/xray/error.log", 
                "loglevel": "info" 
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
                      { "id": "{{uuid}}", "flow": "xtls-rprx-vision", "email": "Админ" }
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
                { "protocol": "blackhole", "tag": "block" }
              ],
              "routing": {
                "rules": [
                  { "inboundTag": ["api"], "outboundTag": "api", "type": "field" }
                ]
              }
            }
            """;

            string base64Json = Convert.ToBase64String(Encoding.UTF8.GetBytes(configJson));
            await ssh.ExecuteCommandAsync($"echo '{base64Json}' | base64 -d > /tmp/config_test.json");

            var testResult = await ssh.ExecuteCommandAsync("/usr/local/bin/xray run -test -config /tmp/config_test.json");

            if (!testResult.Contains("Configuration OK"))
            {
                return (false, "ОШИБКА: Ядро отклонило сгенерированный конфиг!", "");
            }

            // ИСПРАВЛЕНИЕ: Бронебойное создание файлов логов с максимальными правами!
            await ssh.ExecuteCommandAsync("mkdir -p /var/log/xray");
            await ssh.ExecuteCommandAsync("touch /var/log/xray/access.log /var/log/xray/error.log");
            await ssh.ExecuteCommandAsync("chmod -R 777 /var/log/xray");
            await ssh.ExecuteCommandAsync("chown -R nobody:nogroup /var/log/xray 2>/dev/null || true");

            await ssh.ExecuteCommandAsync("mv /tmp/config_test.json /usr/local/etc/xray/config.json");
            await ssh.ExecuteCommandAsync("systemctl restart xray");

            string vlessLink = $"vless://{uuid}@{serverIp}:443?security=reality&encryption=none&pbk={pubKey}&headerType=none&fp=chrome&type=tcp&flow=xtls-rprx-vision&sni={sni}&sid={shortId}#KoFFPanel-{serverIp}";

            return (true, "VLESS-Reality настроен!", vlessLink);
        }
        catch (Exception ex)
        {
            return (false, $"КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}", "");
        }
    }
}