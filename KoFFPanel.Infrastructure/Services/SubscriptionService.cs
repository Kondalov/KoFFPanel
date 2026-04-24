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
            _logger.Log("SUB", "Настройка кастомного Python-микросервиса подписок...");
            string s = (await ssh.ExecuteCommandAsync("if [ \"$EUID\" -ne 0 ]; then echo 'sudo'; fi")).Trim();

            await ssh.ExecuteCommandAsync($"{s} fuser -k 8080/tcp 2>/dev/null || true");
            await ssh.ExecuteCommandAsync($"if command -v ufw >/dev/null 2>&1; then {s} ufw allow 8080/tcp || true; fi");
            await ssh.ExecuteCommandAsync($"{s} iptables -I INPUT 1 -p tcp --dport 8080 -j ACCEPT || true");
            await ssh.ExecuteCommandAsync($"{s} sh -c 'iptables-save > /etc/iptables/rules.v4' 2>/dev/null || true");

            await ssh.ExecuteCommandAsync($"{s} mkdir -p /var/www/xray-sub");
            await ssh.ExecuteCommandAsync($"{s} chown -R $USER:$USER /var/www/xray-sub");
            await ssh.ExecuteCommandAsync($"{s} chmod -R 755 /var/www/xray-sub");

            // ИСПРАВЛЕНИЕ: Кастомный BaseHTTPRequestHandler. Успешно отдает файлы без расширений (UUID).
            string pyScript = @"
import http.server, socketserver, os

class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        p = self.path.strip('/')
        if not p or '/' in p:
            self.send_response(404)
            self.end_headers()
            return
        fpath = '/var/www/xray-sub/' + p
        if os.path.isfile(fpath):
            with open(fpath, 'rb') as f:
                d = f.read()
            self.send_response(200)
            self.send_header('Content-Type', 'text/plain; charset=utf-8')
            self.send_header('Cache-Control', 'no-store, no-cache, must-revalidate, max-age=0')
            self.send_header('profile-update-interval', '24')
            self.send_header('profile-title', 'KoFFPanel')
            self.end_headers()
            self.wfile.write(d)
        else:
            self.send_response(404)
            self.end_headers()

socketserver.TCPServer.allow_reuse_address = True
with socketserver.ThreadingTCPServer(('', 8080), H) as d:
    d.serve_forever()
".Replace("\r", "");

            string b64Py = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(pyScript));
            await ssh.ExecuteCommandAsync($"echo '{b64Py}' | base64 -d | {s} tee /var/www/xray-sub/server.py >/dev/null");

            string service = @"[Unit]
Description=KoFFPanel Sub Service
After=network.target
[Service]
Type=simple
User=root
ExecStart=/usr/bin/python3 /var/www/xray-sub/server.py
Restart=always
RestartSec=3
[Install]
WantedBy=multi-user.target";

            string b64Svc = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(service));
            await ssh.ExecuteCommandAsync($"echo '{b64Svc}' | base64 -d | {s} tee /etc/systemd/system/koff-sub.service >/dev/null");

            await ssh.ExecuteCommandAsync($"{s} systemctl daemon-reload && {s} systemctl enable koff-sub --now && {s} systemctl restart koff-sub");

            return true;
        }
        catch (Exception ex)
        {
            _logger.Log("SUB-ERROR", $"Ошибка микросервиса: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> UpdateUserSubscriptionAsync(ISshService ssh, string uuid, IEnumerable<string> links)
    {
        if (!ssh.IsConnected || string.IsNullOrEmpty(uuid)) return false;
        try
        {
            string s = (await ssh.ExecuteCommandAsync("if [ \"$EUID\" -ne 0 ]; then echo 'sudo'; fi")).Trim();

            // ИСПРАВЛЕНИЕ ЛОГИКИ: Жесткая проверка статуса!
            string checkSvc = (await ssh.ExecuteCommandAsync("systemctl is-active koff-sub")).Trim();
            if (checkSvc != "active")
            {
                _logger.Log("SUB-WARN", "Служба подписок не активна! Выполняем авто-восстановление...");
                await InitializeServerAsync(ssh);
            }

            var validLinks = links != null ? links.Where(l => !string.IsNullOrWhiteSpace(l)).ToList() : new List<string>();
            string combinedLinks = !validLinks.Any()
                ? "vless://00000000-0000-0000-0000-000000000000@127.0.0.1:443?encryption=none&security=none&type=tcp#KoFFPanel_Wait\n"
                : string.Join("\n", validLinks) + "\n";

            string finalBase64Payload = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(combinedLinks));
            _logger.Log("DEEP-TRACE", $"Base64 Payload сгенерирован. Длина: {finalBase64Payload.Length}");

            string tempPath = $"/var/www/xray-sub/{uuid}.tmp";
            string finalPath = $"/var/www/xray-sub/{uuid}";

            await ssh.ExecuteCommandAsync($"printf '%s' '{finalBase64Payload}' | {s} tee {tempPath} >/dev/null && {s} mv {tempPath} {finalPath}");

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