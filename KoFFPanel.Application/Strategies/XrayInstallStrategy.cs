using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Templates;
using System;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Strategies;

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

public class XrayInstallStrategy : ICoreInstallStrategy
{
    public async Task<(bool Success, string Message, object? Result)> ExecuteFullInstall(
        ISshService ssh,
        string ipAddress,
        int vpnPort,
        string sni,
        string existingUuid = "",
        string existingPrivKey = "",
        string existingPubKey = "",
        string existingShortId = "")
    {
        try
        {
            await ssh.ExecuteCommandAsync("apt-get update -q && apt-get install -y curl unzip lsof uuid-runtime openssl");

            string cleanupScript = """
            systemctl stop xray 2>/dev/null || true
            systemctl disable xray 2>/dev/null || true
            killall -9 xray 2>/dev/null || true
            rm -f /etc/systemd/system/xray.service
            rm -rf /usr/local/bin/xray /etc/xray /usr/local/etc/xray /var/log/xray
            systemctl daemon-reload
            """;
            await ssh.ExecuteCommandAsync(cleanupScript);

            var pids = await ssh.ExecuteCommandAsync($"lsof -t -i:{vpnPort} || true");
            if (!string.IsNullOrWhiteSpace(pids)) await ssh.ExecuteCommandAsync($"kill -9 {pids.Trim().Replace('\n', ' ')}");

            await ssh.ExecuteCommandAsync("mkdir -p /usr/local/etc/xray /var/log/xray");
            await ssh.ExecuteCommandAsync("curl -L -s -o /tmp/xray.zip https://github.com/XTLS/Xray-core/releases/latest/download/Xray-linux-64.zip");
            await ssh.ExecuteCommandAsync("unzip -o -q /tmp/xray.zip -d /usr/local/bin/ xray");
            await ssh.ExecuteCommandAsync("chmod +x /usr/local/bin/xray");

            // ПЕРЕИСПОЛЬЗОВАНИЕ КЛЮЧЕЙ ИЗ БАЗЫ
            string finalUuid = existingUuid;
            string finalPriv = existingPrivKey;
            string finalPub = existingPubKey;
            string finalSid = existingShortId;

            // Если ключей нет - генерируем
            if (string.IsNullOrWhiteSpace(finalPriv) || string.IsNullOrWhiteSpace(finalPub))
            {
                string keyGenScript = """
                UUID=$(uuidgen)
                KEYS=$(/usr/local/bin/xray x25519)
                PRIV=$(echo "$KEYS" | grep -i "Private" | awk '{print $NF}' | tr -d '\r\n')
                PUB=$(echo "$KEYS" | grep -i "Public" | awk '{print $NF}' | tr -d '\r\n')
                SID=$(openssl rand -hex 4)
                echo "$UUID|$PRIV|$PUB|$SID"
                """;

                string keysOutput = await ssh.ExecuteCommandAsync(keyGenScript);
                string[] parts = keysOutput.Trim().Split('|');

                if (parts.Length != 4 || string.IsNullOrWhiteSpace(parts[2]))
                {
                    return (false, "Критическая ошибка генерации ключей x25519.", null);
                }

                finalUuid = parts[0];
                finalPriv = parts[1];
                finalPub = parts[2];
                finalSid = parts[3];
            }

            var result = new XrayInstallResult
            {
                Uuid = finalUuid,
                PrivateKey = finalPriv,
                PublicKey = finalPub,
                ShortId = finalSid,
                IpAddress = ipAddress,
                Port = vpnPort,
                Sni = sni
            };

            string configJson = XrayRealityConfigTemplate.Generate("IPv4", vpnPort, result.Uuid, sni, result.PrivateKey, result.ShortId);
            string safeConfig = configJson.Replace("\"", "\\\"");
            await ssh.ExecuteCommandAsync($"echo \"{safeConfig}\" > /usr/local/etc/xray/config.json");

            string service = "[Unit]\nDescription=Xray\nAfter=network.target\n[Service]\nExecStart=/usr/local/bin/xray run -config /usr/local/etc/xray/config.json\nRestart=always\n[Install]\nWantedBy=multi-user.target";
            await ssh.ExecuteCommandAsync($"echo -e \"{service}\" > /etc/systemd/system/xray.service");
            await ssh.ExecuteCommandAsync("systemctl daemon-reload && systemctl enable xray && systemctl restart xray");

            string status = await ssh.ExecuteCommandAsync("systemctl is-active xray");
            if (status.Trim() != "active")
            {
                return (false, "Критическая ошибка: Xray упал при запуске.", null);
            }

            return (true, "🚀 Сервер Xray Reality успешно настроен!", result);
        }
        catch (Exception ex)
        {
            return (false, $"Сбой инсталлятора: {ex.Message}", null);
        }
    }
}