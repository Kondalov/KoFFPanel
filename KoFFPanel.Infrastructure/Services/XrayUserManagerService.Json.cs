using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KoFFPanel.Application.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace KoFFPanel.Infrastructure.Services;

public partial class XrayUserManagerService
{
    private async Task RebuildInboundsAsync(JsonNode root, string serverIp, ISshService ssh)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        var inbounds = root?["inbounds"]?.AsArray();
        if (inbounds == null) return;

        bool hasReality = false;
        bool hasXHttp = false;
        foreach (var inbound in inbounds.OfType<JsonObject>()) {
            if ("vless".Equals(inbound["protocol"]?.ToString(), StringComparison.OrdinalIgnoreCase)) {
                var net = inbound["streamSettings"]?["network"]?.ToString();
                if ("tcp".Equals(net, StringComparison.OrdinalIgnoreCase)) hasReality = true;
                if ("xhttp".Equals(net, StringComparison.OrdinalIgnoreCase) || "quic".Equals(net, StringComparison.OrdinalIgnoreCase)) hasXHttp = true;
            }
        }

        foreach (var inbound in inbounds.OfType<JsonObject>()) {
            if ("vless".Equals(inbound["protocol"]?.ToString(), StringComparison.OrdinalIgnoreCase)) {
                var net = inbound["streamSettings"]?["network"]?.ToString();
                bool isXHttp = "xhttp".Equals(net, StringComparison.OrdinalIgnoreCase) || "quic".Equals(net, StringComparison.OrdinalIgnoreCase);
                
                var clients = new JsonArray();
                var targetUsers = dbUsers.Where(u => u.IsActive && (!isXHttp ? u.IsVlessEnabled : u.IsTrustTunnelEnabled));

                foreach (var u in targetUsers)
                {
                    var clientObj = new JsonObject { ["id"] = u.Uuid, ["email"] = u.Email };
                    if (!isXHttp) clientObj["flow"] = "xtls-rprx-vision";
                    clients.Add(clientObj);
                }

                if (inbound["settings"] is JsonObject s) s["clients"] = clients;
                await UpdateXrayLinksAsync(inbound, dbUsers, serverIp, ssh, isXHttp);
            }
        }

        // Если Reality не найден в конфиге, помечаем это в ссылках
        if (!hasReality) 
        {
            foreach (var u in dbUsers) 
            {
                if (string.IsNullOrEmpty(u.VlessLink) || u.VlessLink.Contains("не установлен"))
                    u.VlessLink = "VLESS-Reality не найден в конфиге сервера";
            }
        }

        foreach (var u in dbUsers) 
        {
            u.Hysteria2Link = "Не поддерживается в Xray (используйте Sing-Box)";
            if (!hasXHttp) u.TrustTunnelLink = "TrustTunnel не активен (установлен Xray-Reality)";
        }
        await _dbContext.SaveChangesAsync();
    }

    private async Task UpdateXrayLinksAsync(JsonObject inbound, List<KoFFPanel.Domain.Entities.VpnClient> dbUsers, string serverIp, ISshService ssh, bool isQuic)
    {
        int port = (int?)inbound["port"] ?? (isQuic ? 4433 : 443);
        string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;

        if (!isQuic)
        {
            var rs = inbound["streamSettings"]?["realitySettings"];
            string sid = rs?["shortIds"]?[0]?.ToString() ?? "";

            // ИСПРАВЛЕНИЕ: Fallback должен строго совпадать с тем, что генерирует ядро (www.microsoft.com)
            string sni = rs?["serverNames"]?[0]?.ToString() ?? "www.microsoft.com";

            string pk = rs?["privateKey"]?.ToString() ?? "";
            string pub = "";

            if (!string.IsNullOrEmpty(pk))
            {
                var outStr = await ssh.ExecuteCommandAsync($"/usr/local/bin/xray x25519 -i {pk}");
                var m = System.Text.RegularExpressions.Regex.Match(outStr, @"(?i)PublicKey[)]?\s*:\s*(\S+)");
                if (m.Success) pub = m.Groups[1].Value.Trim();
            }

            foreach (var u in dbUsers)
            {
                string encodedName = Uri.EscapeDataString($"KoFFPanel_{u.Email}");
                u.VlessLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=tcp&security=reality&pbk={pub}&fp=chrome&sni={sni}&sid={sid}&spx=%2F&flow=xtls-rprx-vision&alpn=h2#{encodedName}";
            }
        }
        else
        {
            string sni = inbound["streamSettings"]?["tlsSettings"]?["serverName"]?.ToString() ?? "www.microsoft.com";
            foreach (var u in dbUsers)
            {
                string encodedName = Uri.EscapeDataString($"TrustTunnel_{u.Email}");
                u.TrustTunnelLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=xhttp&security=tls&encryption=none&sni={sni}&alpn=h3&host={sni}&path=%2F&allowInsecure=1&insecure=1#{encodedName}";
            }
        }
    }

    private async Task<(bool IsSuccess, string Message)> ApplyAndTestConfigAsync(ISshService ssh, string newJson)
    {
        string s = (await ssh.ExecuteCommandAsync("if [ \"$EUID\" -ne 0 ]; then echo 'sudo'; fi")).Trim();

        // ИСПРАВЛЕНИЕ: Добавлен StringComparison для соответствия строгим правилам .NET 10
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(newJson.Replace("\r", "", StringComparison.OrdinalIgnoreCase)));

        await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d | {s} tee /tmp/config_users_test.json >/dev/null");

        string testResult = await ssh.ExecuteCommandAsync("/usr/local/bin/xray run -test -config /tmp/config_users_test.json 2>&1");

        // ИСПРАВЛЕНИЕ: Добавлен StringComparison.OrdinalIgnoreCase в метод Contains
        if (!testResult.Contains("Configuration OK", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Ошибка теста Xray! Конфиг не прошел валидацию.");
        }

        string applyCmd = $"{s} cp /usr/local/etc/xray/config.json /usr/local/etc/xray/config.backup.json; " +
                          $"{s} mv /tmp/config_users_test.json /usr/local/etc/xray/config.json; " +
                          $"{s} systemctl stop xray 2>/dev/null; " +
                          $"{s} killall -9 xray 2>/dev/null; " +
                          $"{s} systemctl restart xray";

        await ssh.ExecuteCommandAsync(applyCmd);

        return (true, "Обновлено!");
    }
}
