using KoFFPanel.Application.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly IAppLogger _logger;
    private string? _customDomain;

    public SubscriptionService(IAppLogger logger)
    {
        _logger = logger;
    }

    public void SetCustomDomain(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain))
        {
            _customDomain = null;
            return;
        }

        _customDomain = domain.Trim().TrimEnd('/');
        if (!_customDomain.StartsWith("http"))
        {
            _customDomain = "https://" + _customDomain;
        }
    }

    public async Task<bool> InitializeServerAsync(ISshService ssh)
    {
        if (!ssh.IsConnected) return false;
        try
        {
            _logger.Log("SUB", "Настройка кастомного Python-микросервиса подписок (HTTPS Ready)...");
            string s = (await ssh.ExecuteCommandAsync("if [ \"$EUID\" -ne 0 ]; then echo 'sudo'; fi")).Trim();

            await ssh.ExecuteCommandAsync($"{s} fuser -k 8080/tcp 2>/dev/null || true");
            await ssh.ExecuteCommandAsync($"if command -v ufw >/dev/null 2>&1; then {s} ufw allow 8080/tcp || true; fi");
            await ssh.ExecuteCommandAsync($"{s} iptables -I INPUT 1 -p tcp --dport 8080 -j ACCEPT || true");

            await ssh.ExecuteCommandAsync($"{s} mkdir -p /var/www/xray-sub");
            await ssh.ExecuteCommandAsync($"{s} chown -R $USER:$USER /var/www/xray-sub");
            await ssh.ExecuteCommandAsync($"{s} chmod -R 755 /var/www/xray-sub");

            // УЛУЧШЕННЫЙ СКРИПТ: Добавлена обработка ошибок и логирование
            string pyScript = @"
import http.server, socketserver, os, sys

class H(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        try:
            p = self.path.strip('/')
            if not p or '/' in p:
                self.send_error(404, 'Not Found')
                return
            
            fpath = os.path.join('/var/www/xray-sub/', p)
            if os.path.isfile(fpath):
                with open(fpath, 'rb') as f:
                    content = f.read()
                
                self.send_response(200)
                self.send_header('Content-Type', 'text/plain; charset=utf-8')
                self.send_header('Content-Length', str(len(content)))
                self.send_header('Cache-Control', 'no-store, no-cache, must-revalidate, max-age=0')
                self.send_header('profile-update-interval', '24')
                self.send_header('profile-title', 'KoFFPanel')
                self.send_header('subscription-userinfo', 'upload=0; download=0; total=0; expire=0')
                self.end_headers()
                self.wfile.write(content)
            else:
                self.send_error(404, 'File Not Found')
        except Exception as e:
            print(f'Error handling request: {e}')
            self.send_error(500, 'Internal Server Error')

    def log_message(self, format, *args):
        # Тихий режим для уменьшения логов в системном журнале
        pass

socketserver.TCPServer.allow_reuse_address = True
try:
    with socketserver.ThreadingTCPServer(('', 8080), H) as d:
        print('Starting subscription server on port 8080...')
        d.serve_forever()
except Exception as e:
    print(f'Fatal server error: {e}')
    sys.exit(1)
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
        if (!string.IsNullOrEmpty(_customDomain))
        {
            return $"{_customDomain}/{uuid}";
        }
        return $"http://{serverIp}:8080/{uuid}";
    }
}