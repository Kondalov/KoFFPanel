using KoFFPanel.Application.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
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

            // Настройка Nginx. Убрали强制 text/plain, так как отдаем Base64
            string nginxConfig = @"
server {
    listen 8080;
    server_name _;
    root /var/www/xray-sub;
    
    location / {
        try_files $uri $uri/ =404;
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

    public async Task<bool> UpdateUserSubscriptionAsync(ISshService ssh, string uuid, IEnumerable<string> links)
    {
        if (!ssh.IsConnected || links == null || !links.Any() || string.IsNullOrEmpty(uuid)) return false;
        try
        {
            // ИСПРАВЛЕНИЕ АРХИТЕКТУРЫ ПОДПИСОК (Hiddify / v2rayNG Standard)
            // 1. Отбрасываем пустые ссылки (например, если протокол выключен в БД)
            var validLinks = links.Where(l => !string.IsNullOrWhiteSpace(l));

            // 2. Склеиваем все ссылки, каждая с новой строки
            string combinedLinks = string.Join("\n", validLinks);

            // 3. Кодируем ВЕСЬ текстовый блок в Base64. 
            // Это мировой стандарт для VPN-подписок. Hiddify сам скачает Base64 файл и раскодирует его.
            string finalBase64Payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(combinedLinks));

            // 4. Пишем готовый Base64 прямо в файл (без расшифровки на сервере)
            await ssh.ExecuteCommandAsync($"echo '{finalBase64Payload}' > /var/www/xray-sub/{uuid}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Log("SUB-ERROR", $"Ошибка обновления подписки: {ex.Message}");
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
        return $"http://{serverIp}:8080/{uuid}";
    }
}