using KoFFPanel.Application.Interfaces;
using System;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly IAppLogger _logger;

    public SubscriptionService(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<bool> InitializeServerAsync(ISshService ssh)
    {
        if (!ssh.IsConnected) return false;
        try
        {
            _logger.Log("SUB", "Настройка Nginx для Hiddify-совместимых подписок...");
            await ssh.ExecuteCommandAsync("apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y nginx");

            // ИСПРАВЛЕНИЕ: Добавляем строгую кодировку utf-8, которую требует Hiddify
            string nginxConfig = @"
server {
    listen 8080;
    server_name _;
    root /var/www/xray-sub;
    
    location / {
        try_files $uri $uri/ =404;
        default_type text/plain;
        add_header Content-Type ""text/plain; charset=utf-8"";
    }
}";
            string base64Config = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(nginxConfig));
            await ssh.ExecuteCommandAsync($"echo '{base64Config}' | base64 -d > /etc/nginx/sites-available/xray_sub");
            await ssh.ExecuteCommandAsync("ln -sf /etc/nginx/sites-available/xray_sub /etc/nginx/sites-enabled/");

            await ssh.ExecuteCommandAsync("mkdir -p /var/www/xray-sub");
            await ssh.ExecuteCommandAsync("chmod -R 755 /var/www/xray-sub");
            await ssh.ExecuteCommandAsync("systemctl restart nginx");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log("SUB-ERROR", $"Ошибка Nginx: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UpdateUserSubscriptionAsync(ISshService ssh, string uuid, string vlessLink)
    {
        if (!ssh.IsConnected || string.IsNullOrEmpty(vlessLink) || string.IsNullOrEmpty(uuid)) return false;
        try
        {
            // ИСПРАВЛЕНИЕ ДЛЯ HIDDIFY: Отдаем RAW текст без Base64!
            // Экранируем ссылку через Base64 локально, чтобы bash echo не сломался от спецсимволов (&, =, #),
            // а на сервере декодируем обратно в сырой vless:// текст и сохраняем под именем UUID.
            string safeBase64Link = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(vlessLink));
            await ssh.ExecuteCommandAsync($"echo '{safeBase64Link}' | base64 -d > /var/www/xray-sub/{uuid}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> DeleteUserSubscriptionAsync(ISshService ssh, string uuid)
    {
        if (!ssh.IsConnected || string.IsNullOrEmpty(uuid)) return false;
        try
        {
            await ssh.ExecuteCommandAsync($"rm -f /var/www/xray-sub/{uuid}");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public string GetSubscriptionUrl(string serverIp, string uuid)
    {
        // Теперь ссылка выглядит круто и безопасно: http://IP:8080/b9b3e1a0-5b5c...
        return $"http://{serverIp}:8080/{uuid}";
    }
}