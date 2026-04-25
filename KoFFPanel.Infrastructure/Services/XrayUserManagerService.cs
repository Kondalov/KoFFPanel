using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public partial class XrayUserManagerService : IXrayUserManagerService
{
    private readonly IAppLogger _logger;
    private readonly AppDbContext _dbContext;

    public XrayUserManagerService(IAppLogger logger, AppDbContext dbContext)
    {
        _logger = logger; _dbContext = dbContext;
    }

    public async Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        var globalUsers = await _dbContext.Clients.Where(c => c.ServerIp != serverIp).ToListAsync();
        bool isMigrated = false;

        foreach (var gu in globalUsers.GroupBy(u => u.Email).Select(g => g.First()))
        {
            if (!dbUsers.Any(u => u.Email == gu.Email))
            {
                var newUser = new VpnClient 
                { 
                    Email = gu.Email, 
                    Uuid = string.IsNullOrEmpty(gu.Uuid) ? Guid.NewGuid().ToString() : gu.Uuid, 
                    ServerIp = serverIp, 
                    Protocol = gu.Protocol, 
                    TrafficLimit = gu.TrafficLimit, 
                    ExpiryDate = gu.ExpiryDate, 
                    IsActive = gu.IsActive, 
                    IsP2PBlocked = gu.IsP2PBlocked, 
                    IsVlessEnabled = gu.IsVlessEnabled,
                    IsHysteria2Enabled = gu.IsHysteria2Enabled,
                    IsTrustTunnelEnabled = gu.IsTrustTunnelEnabled
                };
                _dbContext.Clients.Add(newUser);
                dbUsers.Add(newUser);
                isMigrated = true;
            }
        }

        // Create Admin if NO users exist globally and locally
        if (dbUsers.Count == 0 && globalUsers.Count == 0)
        {
            // ИСПРАВЛЕНИЕ: Для Xray сервиса по умолчанию включаем ТОЛЬКО VLESS.
            // TrustTunnel и Hysteria2 должны быть выключены, если они не настроены.
            var admin = new VpnClient 
            { 
                Email = "ADMIN", 
                Uuid = Guid.NewGuid().ToString(), 
                ServerIp = serverIp, 
                Protocol = "VLESS", 
                IsActive = true, 
                IsP2PBlocked = true, 
                IsVlessEnabled = true, 
                IsTrustTunnelEnabled = false, 
                IsHysteria2Enabled = false 
            };
            _dbContext.Clients.Add(admin);
            dbUsers.Add(admin);
            isMigrated = true;
        }

        // ИСПРАВЛЕНИЕ: Удаление дублей админа (ADMIN vs Админ)
        var oldAdmin = dbUsers.FirstOrDefault(u => u.Email == "Админ");
        var standardAdmin = dbUsers.FirstOrDefault(u => u.Email == "ADMIN");

        if (oldAdmin != null)
        {
            if (standardAdmin == null)
            {
                oldAdmin.Email = "ADMIN";
                isMigrated = true;
            }
            else
            {
                _dbContext.Clients.Remove(oldAdmin);
                dbUsers.Remove(oldAdmin);
                isMigrated = true;
            }
        }

        if (isMigrated) await _dbContext.SaveChangesAsync();

        if (!ssh.IsConnected) return dbUsers;
        string raw = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null");
        if (string.IsNullOrWhiteSpace(raw) || raw.Contains("No such")) return dbUsers;
        try {
            var root = JsonNode.Parse(raw);
            if (root == null) return dbUsers;

            foreach (var inbound in root["inbounds"]?.AsArray().OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>()) {
                if (inbound["protocol"]?.ToString() == "vless") {
                    foreach (var c in inbound["settings"]?["clients"]?.AsArray() ?? new JsonArray()) {
                        string email = c?["email"]?.ToString() ?? "Unknown";
                        if (!dbUsers.Any(u => u.Email == email)) {
                            var newUser = new VpnClient { Email = email, Uuid = c?["id"]?.ToString() ?? "", ServerIp = serverIp, Protocol = "VLESS", IsActive = true, IsP2PBlocked = true, IsVlessEnabled = true };
                            _dbContext.Clients.Add(newUser); dbUsers.Add(newUser);
                        }
                    }
                }
            }
            await _dbContext.SaveChangesAsync(); await RebuildInboundsAsync(root, serverIp, ssh);
            var res = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        } catch { return dbUsers; }
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

            string sni = "www.microsoft.com";
            string encodedName = Uri.EscapeDataString($"KoFFPanel_{serverIp}");

            // ИСПРАВЛЕНИЕ: loglevel изменен на "info" для сбора расширенной телеметрии (диагностика Тайм-аутов)
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

            string vlessLink = $"vless://{uuid}@{serverIp}:443?security=reality&encryption=none&alpn=h2,http/1.1&pbk={pubKey}&headerType=none&fp=chrome&type=tcp&flow=xtls-rprx-vision&sni={sni}&sid={shortId}#{encodedName}";

            return (true, "VLESS-Reality настроен!", vlessLink);
        }
        catch (Exception ex)
        {
            return (false, $"КРИТИЧЕСКАЯ ОШИБКА: {ex.Message}", "");
        }
    }

    public async Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string email, long limit, DateTime? expiry, bool isP2PBlocked = true)
    {
        try { SshGuard.ThrowIfInvalid(email, null); } catch (Exception ex) { return (false, ex.Message, ""); }
        if (await _dbContext.Clients.AnyAsync(c => c.Email == email && c.ServerIp == serverIp)) return (false, "Уже есть!", "");

        // ИСПРАВЛЕНИЕ: Для Xray сервиса включаем по умолчанию ТОЛЬКО VLESS.
        var user = new VpnClient 
        { 
            Email = email, 
            Uuid = Guid.NewGuid().ToString(), 
            ServerIp = serverIp, 
            Protocol = "VLESS", 
            TrafficLimit = limit, 
            ExpiryDate = expiry, 
            IsActive = true, 
            IsP2PBlocked = isP2PBlocked, 
            IsVlessEnabled = true, 
            IsHysteria2Enabled = false, 
            IsTrustTunnelEnabled = false 
        };
        _dbContext.Clients.Add(user); await _dbContext.SaveChangesAsync();

        if (ssh.IsConnected)
        {
            var rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null");
            if (!string.IsNullOrWhiteSpace(rawJson) && rawJson.Contains("{"))
            {
                var root = JsonNode.Parse(rawJson);
                if (root != null)
                {
                    await RebuildInboundsAsync(root, serverIp, ssh);
                    var res = await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    if (!res.IsSuccess) return (false, res.Message, "");
                }
            }
        }
        
        var savedUser = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
        return (true, "Пользователь добавлен!", savedUser?.VlessLink ?? "");
    }

    public async Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string email)
    {
        if (email.Equals("Админ", StringComparison.OrdinalIgnoreCase)) return (false, "Нельзя удалить Админа!");
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
        if (await _dbContext.Clients.CountAsync(c => c.ServerIp == serverIp) <= 1) return (false, "Последний!");
        if (user != null) { _dbContext.Clients.Remove(user); await _dbContext.SaveChangesAsync(); }
        
        var rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json 2>/dev/null");
        if (string.IsNullOrWhiteSpace(rawJson)) return (true, "Удален.");
        var root = JsonNode.Parse(rawJson);
        if (root == null) return (true, "Удален.");

        await RebuildInboundsAsync(root, serverIp, ssh);
        return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh)
    {
        var stats = new Dictionary<string, long>();
        try {
            var raw = await ssh.ExecuteCommandAsync("/usr/local/bin/xray api statsquery --server=127.0.0.1:10085");
            var root = JsonNode.Parse(raw);
            if (root == null) return stats;

            foreach (var item in root["stat"]?.AsArray() ?? new JsonArray()) {
                var parts = item?["name"]?.ToString().Split(">>>");
                if (parts?.Length == 4 && parts[0] == "user") {
                    string email = parts[1]; long val = long.Parse(item?["value"]?.ToString() ?? "0");
                    if (stats.ContainsKey(email)) stats[email] += val; else stats[email] = val;
                }
            }
        } catch { }
        return stats;
    }

    public async Task<bool> ResetTrafficAsync(ISshService ssh, string email) { try { await ssh.ExecuteCommandAsync($"/usr/local/bin/xray api statsquery --server=127.0.0.1:10085 --pattern \"user>>>{email}>>>traffic\" --reset"); return true; } catch { return false; } }
    public async Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string email, bool active)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
        if (user == null) return (false, "Нет в БД");
        user.IsActive = active; await _dbContext.SaveChangesAsync();
        
        var rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json");
        var root = JsonNode.Parse(rawJson);
        if (root == null) return (false, "Ошибка конфига");

        await RebuildInboundsAsync(root, serverIp, ssh);
        return await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    public async Task<bool> UpdateUserLimitsAsync(string serverIp, string email, long limit, DateTime? expiry)
    {
        var user = await _dbContext.Clients.FirstOrDefaultAsync(c => c.ServerIp == serverIp && c.Email == email);
        if (user == null) return false;
        user.TrafficLimit = limit; user.ExpiryDate = expiry; await _dbContext.SaveChangesAsync(); return true;
    }

    public async Task SaveTrafficToDbAsync(string ip, IEnumerable<VpnClient> clients)
    {
        var users = await _dbContext.Clients.Where(c => c.ServerIp == ip).ToListAsync();
        foreach (var c in clients) { var u = users.FirstOrDefault(x => x.Email == c.Email); if (u != null) u.TrafficUsed = c.TrafficUsed; }
        await _dbContext.SaveChangesAsync();
    }

    public async Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> dbUsers)
    {
        try {
            var rawJson = await ssh.ExecuteCommandAsync("cat /usr/local/etc/xray/config.json");
            var root = JsonNode.Parse(rawJson);
            if (root == null) return false;

            string ip = dbUsers.FirstOrDefault()?.ServerIp ?? "";
            if (!string.IsNullOrEmpty(ip)) await RebuildInboundsAsync(root, ip, ssh);
            return (await ApplyAndTestConfigAsync(ssh, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }))).IsSuccess;
        } catch { return false; }
    }
}
