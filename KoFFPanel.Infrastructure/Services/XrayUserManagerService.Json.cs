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

        bool hasReality = false, hasTrustTunnel = false;
        foreach (var inbound in inbounds.OfType<JsonObject>()) {
            if (inbound["protocol"]?.ToString() == "vless") {
                var net = inbound["streamSettings"]?["network"]?.ToString();
                if (net == "quic" || net == "xhttp") hasTrustTunnel = true;
                else if (net == "tcp") hasReality = true;
            }
        }

        foreach (var inbound in inbounds.OfType<JsonObject>()) {
            if (inbound["protocol"]?.ToString() == "vless") {
                var net = inbound["streamSettings"]?["network"]?.ToString();
                var isQuic = net == "quic" || net == "xhttp";
                var clients = new JsonArray();
                foreach (var u in dbUsers.Where(u => u.IsActive && ((!isQuic && u.IsVlessEnabled) || (isQuic && u.IsTrustTunnelEnabled))))
                    clients.Add(isQuic ? new JsonObject { ["id"] = u.Uuid, ["email"] = u.Email } : new JsonObject { ["id"] = u.Uuid, ["flow"] = "xtls-rprx-vision", ["email"] = u.Email });
                if (inbound["settings"] is JsonObject s) s["clients"] = clients;
                await UpdateXrayLinksAsync(inbound, dbUsers, serverIp, ssh, isQuic);
            }
        }

        if (!hasReality) foreach (var u in dbUsers) u.VlessLink = "VLESS не установлен!";
        if (!hasTrustTunnel) foreach (var u in dbUsers) u.TrustTunnelLink = "TrustTunnel не установлен!";
        await _dbContext.SaveChangesAsync();
    }

    private async Task UpdateXrayLinksAsync(JsonObject inbound, List<KoFFPanel.Domain.Entities.VpnClient> dbUsers, string serverIp, ISshService ssh, bool isQuic)
    {
        int port = (int?)inbound["port"] ?? (isQuic ? 4433 : 443);
        string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
        if (!isQuic) {
            var rs = inbound["streamSettings"]?["realitySettings"];
            string sid = rs?["shortIds"]?[0]?.ToString() ?? ""; string sni = rs?["serverNames"]?[0]?.ToString() ?? "www.microsoft.com";
            string pk = rs?["privateKey"]?.ToString() ?? ""; string pub = "";
            if (!string.IsNullOrEmpty(pk)) {
                var outStr = await ssh.ExecuteCommandAsync($"/usr/local/bin/xray x25519 -i {pk}");
                var m = System.Text.RegularExpressions.Regex.Match(outStr, @"(?i)key\s*:\s*(\S+)");
                if (m.Success) pub = m.Groups[1].Value.Trim();
            }
            foreach (var u in dbUsers) u.VlessLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=tcp&security=reality&encryption=none&pbk={pub}&headerType=none&fp=chrome&sni={sni}&sid={sid}&flow=xtls-rprx-vision#Xray_{u.Email}";
        } else {
            string sni = inbound["streamSettings"]?["tlsSettings"]?["serverName"]?.ToString() ?? "adguard.com";
            foreach (var u in dbUsers) u.TrustTunnelLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=xhttp&security=tls&encryption=none&sni={sni}&alpn=h3&host={sni}&path=%2F&allowInsecure=1&insecure=1#TrustTunnel_{u.Email}";
        }
    }

    private async Task<(bool IsSuccess, string Message)> ApplyAndTestConfigAsync(ISshService ssh, string newJson)
    {
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(newJson.Replace("\r", "")));
        await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d > /tmp/config_users_test.json");
        if (!(await ssh.ExecuteCommandAsync("/usr/local/bin/xray run -test -config /tmp/config_users_test.json 2>&1")).Contains("Configuration OK")) return (false, "Ошибка теста Xray!");
        await ssh.ExecuteCommandAsync("cp /usr/local/etc/xray/config.json /usr/local/etc/xray/config.backup.json; mv /tmp/config_users_test.json /usr/local/etc/xray/config.json; systemctl stop xray 2>/dev/null; killall -9 xray 2>/dev/null; systemctl restart xray");
        return (true, "Обновлено!");
    }
}
