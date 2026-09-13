using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class AcmeCertificateService : IAcmeCertificateService
{
    private readonly IAppLogger _logger;

    public AcmeCertificateService(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<(bool IsSuccess, string CertPath, string KeyPath, string Message)> EnsureCertificateAsync(ISshService ssh, string serverIp, string? customDomain)
    {
        if (!ssh.IsConnected)
            return (false, "", "", "Нет подключения по SSH");

        if (string.IsNullOrWhiteSpace(customDomain))
            return (false, "", "", "Домен не указан (используется локальный самоподписанный сертификат)");

        string cleanDomain = customDomain.Trim()
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .TrimEnd('/');

        if (cleanDomain.Contains(':'))
            cleanDomain = cleanDomain.Split(':')[0];

        if (IPAddress.TryParse(cleanDomain, out _))
            return (false, "", "", "Указан IP адрес вместо доменного имени (Let's Encrypt требует FQDN)");

        try
        {
            _logger.Log("ACME", $"Запуск автоматического выпуска Let's Encrypt сертификата для {cleanDomain}...");
            string s = (await ssh.ExecuteCommandAsync("if [ \"$EUID\" -ne 0 ]; then echo 'sudo'; fi")).Trim();

            string certDest = $"/etc/koff/certs/{cleanDomain}.crt";
            string keyDest = $"/etc/koff/certs/{cleanDomain}.key";

            string script = $@"
mkdir -p /etc/koff/certs

# Устанавливаем certbot при необходимости
if ! command -v certbot >/dev/null 2>&1; then
    export DEBIAN_FRONTEND=noninteractive
    apt-get update -q && apt-get install -y certbot >/dev/null 2>&1 || true
fi

if command -v certbot >/dev/null 2>&1; then
    # Останавливаем временно веб-службы на 80 порту для standalone режима
    systemctl stop nginx apache2 2>/dev/null || true
    
    certbot certonly --standalone -d '{cleanDomain}' --non-interactive --agree-tos --register-unsafely-without-email --keep-until-expiring >/tmp/certbot.log 2>&1
    
    LE_PATH='/etc/letsencrypt/live/{cleanDomain}'
    if [ -f ""$LE_PATH/fullchain.pem"" ] && [ -f ""$LE_PATH/privkey.pem"" ]; then
        cp -L ""$LE_PATH/fullchain.pem"" '{certDest}'
        cp -L ""$LE_PATH/privkey.pem"" '{keyDest}'
        chmod 644 '{certDest}'
        chmod 600 '{keyDest}'
        
        # Настройка хука авто-обновления
        mkdir -p /etc/letsencrypt/renewal-hooks/deploy
        cat << 'HOOK_EOF' > /etc/letsencrypt/renewal-hooks/deploy/koff-reload.sh
#!/bin/sh
cp -L /etc/letsencrypt/live/{cleanDomain}/fullchain.pem '{certDest}'
cp -L /etc/letsencrypt/live/{cleanDomain}/privkey.pem '{keyDest}'
systemctl restart sing-box xray 2>/dev/null || true
HOOK_EOF
        chmod +x /etc/letsencrypt/renewal-hooks/deploy/koff-reload.sh
        echo 'ACME_SUCCESS'
        exit 0
    fi
fi
echo 'ACME_FAILED'
".Replace("\r", "");

            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
            string output = await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d | {s} bash", TimeSpan.FromMinutes(2));

            if (output.Contains("ACME_SUCCESS"))
            {
                _logger.Log("ACME-SUCCESS", $"Сертификат Let's Encrypt для {cleanDomain} успешно получен и настроен.");
                return (true, certDest, keyDest, $"Let's Encrypt сертификат для {cleanDomain} успешно выпущен!");
            }

            _logger.Log("ACME-WARN", $"Не удалось выпустить Let's Encrypt для {cleanDomain}. Возможно DNS еще не обновился. Выполнен откат на локальный сертификат.");
            return (false, "", "", "Не удалось выпустить Let's Encrypt сертификат. Использован локальный сертификат.");
        }
        catch (Exception ex)
        {
            _logger.Log("ACME-ERR", $"Ошибка ACME: {ex.Message}");
            return (false, "", "", ex.Message);
        }
    }
}
