using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Templates;
using KoFFPanel.Application.DTOs;
using System;
using System.Text;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Strategies;

public class SingBoxInstallStrategy : ICoreInstallStrategy
{
    public async Task<(bool Success, string Message, object? Result)> ExecuteFullInstall(
        ISshService ssh,
        string ipAddress,
        int vpnPort,
        string sni,
        string existingUuid = "",
        string existingPrivKey = "",
        string existingPubKey = "",
        string existingShortId = "",
        string customDomain = "")
    {
        try
        {
            string prepareScript = $$"""
            export DEBIAN_FRONTEND=noninteractive
            echo "1. Обновление системы..."
            apt-get update -q || true
            apt-get install -y curl jq lsof openssl tar gzip >/dev/null 2>&1 || true
            
            echo "2. Остановка конфликтующих служб..."
            systemctl stop sing-box xray v2ray trusttunnel 2>/dev/null || true
            systemctl disable sing-box xray trusttunnel 2>/dev/null || true
            
            echo "3. Очистка порта {{vpnPort}}..."
            PIDS=$(lsof -t -i:{{vpnPort}} || true)
            if [ -n "$PIDS" ]; then kill -9 $PIDS 2>/dev/null || true; fi
            
            echo "4. Определение архитектуры..."
            ARCH=$(uname -m)
            case "$ARCH" in
              x86_64) DL_ARCH="amd64" ;;
              aarch64) DL_ARCH="arm64" ;;
              armv7l) DL_ARCH="armv7" ;;
              *) echo "ERROR_ARCH: $ARCH"; exit 1 ;;
            esac
            
            echo "5. Поиск версии..."
            TAG=$(curl -sL --connect-timeout 5 https://api.github.com/repos/SagerNet/sing-box/releases/latest | jq -r .tag_name 2>/dev/null)
            if [ -z "$TAG" ] || [ "$TAG" == "null" ]; then TAG="v1.13.11"; fi
            
            URL="https://github.com/SagerNet/sing-box/releases/download/${TAG}/sing-box-${TAG#v}-linux-${DL_ARCH}.tar.gz"
            
            echo "6. Скачивание..."
            curl -sL --retry 3 --connect-timeout 10 -o /tmp/sb.tar.gz "$URL"
            
            if [ ! -s /tmp/sb.tar.gz ]; then
                echo "ERROR_DOWNLOAD: $URL"
                exit 1
            fi
            
            echo "7. Распаковка..."
            rm -rf /tmp/sb_extracted && mkdir -p /tmp/sb_extracted
            tar -xzf /tmp/sb.tar.gz -C /tmp/sb_extracted --strip-components=1
            
            if [ ! -f /tmp/sb_extracted/sing-box ]; then
                echo "ERROR_EXTRACT"
                exit 1
            fi
            
            mv /tmp/sb_extracted/sing-box /usr/local/bin/
            chmod +x /usr/local/bin/sing-box
            
            echo "8. Верификация..."
            if /usr/local/bin/sing-box version > /dev/null 2>&1; then
                echo "SUCCESS_PREPARE"
            else
                echo "ERROR_VERIFY"
                exit 1
            fi
            
            mkdir -p /etc/sing-box /var/log/sing-box
            rm -rf /tmp/sb.tar.gz /tmp/sb_extracted
            """;

            string safeScriptBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(prepareScript.Replace("\r", "")));
            var prepResult = await ssh.ExecuteCommandAsync($"echo '{safeScriptBase64}' | base64 -d | bash", TimeSpan.FromMinutes(5));

            if (prepResult.Contains("ERROR_ARCH")) return (false, $"Ошибка: Архитектура {prepResult} не поддерживается Sing-box.", null);
            if (prepResult.Contains("ERROR_DOWNLOAD")) return (false, $"Ошибка скачивания архива ядра. Проверьте соединение.", null);
            if (!prepResult.Contains("SUCCESS_PREPARE")) return (false, $"Сбой инсталлятора.\nВывод: {prepResult.Trim()}", null);

            string finalUuid = existingUuid, finalPriv = existingPrivKey, finalPub = existingPubKey, finalSid = existingShortId;

            if (string.IsNullOrWhiteSpace(finalPriv) || string.IsNullOrWhiteSpace(finalPub))
            {
                string keyGenScript = """
                KEYS=$(/usr/local/bin/sing-box generate reality-keypair)
                PRIV=$(echo "$KEYS" | grep PrivateKey | awk '{print $2}' | tr -d '\r\n')
                PUB=$(echo "$KEYS" | grep PublicKey | awk '{print $2}' | tr -d '\r\n')
                SID=$(openssl rand -hex 4)
                UUID=$(/usr/local/bin/sing-box generate uuid)
                echo "$UUID|$PRIV|$PUB|$SID"
                """;

                string keyGenBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(keyGenScript.Replace("\r", "")));
                string keysOutput = await ssh.ExecuteCommandAsync($"echo '{keyGenBase64}' | base64 -d | bash", TimeSpan.FromSeconds(30));
                string[] parts = keysOutput.Trim().Split('|');

                if (parts.Length != 4 || string.IsNullOrWhiteSpace(parts[2]))
                    return (false, $"Сбой при генерации ключей Sing-box.\nВывод: {keysOutput}", null);

                finalUuid = parts[0]; finalPriv = parts[1]; finalPub = parts[2]; finalSid = parts[3];
            }

            var result = new SingBoxInstallResult
            {
                Uuid = finalUuid,
                PrivateKey = finalPriv,
                PublicKey = finalPub,
                ShortId = finalSid,
                IpAddress = ipAddress,
                Port = vpnPort,
                Sni = sni,
                CustomDomain = customDomain
            };

            string configJson = SingBoxRealityConfigTemplate.GenerateServerConfig(vpnPort, result.Uuid, sni, result.PrivateKey, result.ShortId);
            string base64Json = Convert.ToBase64String(Encoding.UTF8.GetBytes(configJson.Replace("\r", "")));
            await ssh.ExecuteCommandAsync($"echo '{base64Json}' | base64 -d > /etc/sing-box/config.json");

            // ИСПРАВЛЕНИЕ: Добавлено 2>&1, чтобы ловить фатальные ошибки, уходящие в STDERR
            var checkConfig = await ssh.ExecuteCommandAsync("/usr/local/bin/sing-box check -c /etc/sing-box/config.json 2>&1");
            if (checkConfig.Contains("FATAL", StringComparison.OrdinalIgnoreCase) || checkConfig.Contains("error", StringComparison.OrdinalIgnoreCase))
            {
                return (false, $"ОШИБКА: Ядро Sing-box отклонило конфиг!\nЛог: {checkConfig.Trim()}", null);
            }

            string serviceScript = """
            cat << 'EOF' > /etc/systemd/system/sing-box.service
            [Unit]
            Description=sing-box service
            Documentation=https://sing-box.sagernet.org
            After=network.target nss-lookup.target network-online.target
            
            [Service]
            CapabilityBoundingSet=CAP_NET_ADMIN CAP_NET_BIND_SERVICE
            AmbientCapabilities=CAP_NET_ADMIN CAP_NET_BIND_SERVICE
            ExecStart=/usr/local/bin/sing-box run -c /etc/sing-box/config.json
            ExecReload=/bin/kill -HUP $MAINPID
            ExecStopPost=/bin/sleep 2
            Restart=on-failure
            RestartSec=10
            LimitNOFILE=infinity
            
            [Install]
            WantedBy=multi-user.target
            EOF
            systemctl daemon-reload && systemctl enable sing-box && systemctl restart sing-box
            """;

            string serviceBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(serviceScript.Replace("\r", "")));
            await ssh.ExecuteCommandAsync($"echo '{serviceBase64}' | base64 -d | bash", TimeSpan.FromSeconds(30));

            string status = await ssh.ExecuteCommandAsync("systemctl is-active sing-box");
            if (status.Trim() != "active")
            {
                string logs = await ssh.ExecuteCommandAsync("journalctl -u sing-box -n 20 --no-pager");
                return (false, $"Служба Sing-box не запустилась.\nЛоги: {logs.Trim()}", null);
            }

            return (true, "🚀 Современное ядро Sing-box успешно настроено!", result);
        }
        catch (Exception ex)
        {
            return (false, $"Системный сбой инсталлятора Sing-box: {ex.Message}", null);
        }
    }
}