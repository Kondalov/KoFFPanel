using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using System.IO;

namespace KoFFPanel.Infrastructure.Services;

public partial class SingBoxUserManagerService
{
    private async Task RebuildInboundsAsync(JsonNode root, string serverIp)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        var inbounds = root?["inbounds"]?.AsArray();
        if (inbounds == null) return;

        bool hasReality = false, hasHy2 = false, hasTrustTunnel = false;
        foreach (var inboundNode in inbounds) {
            var type = inboundNode?["type"]?.ToString();
            if (type == "vless") { if (inboundNode?["transport"]?["type"]?.ToString() == "quic") hasTrustTunnel = true; else hasReality = true; }
            else if (type == "hysteria2") hasHy2 = true;
        }

        foreach (var inbound in inbounds.OfType<JsonObject>()) {
            var type = inbound["type"]?.ToString();
            if (type == "vless") {
                var isQuic = inbound["transport"]?["type"]?.ToString() == "quic";
                var usersArray = new JsonArray();
                foreach (var u in dbUsers.Where(u => u.IsActive && ((!isQuic && u.IsVlessEnabled) || (isQuic && u.IsTrustTunnelEnabled))))
                    usersArray.Add(isQuic ? new JsonObject { ["name"] = u.Email, ["uuid"] = u.Uuid } : new JsonObject { ["name"] = u.Email, ["uuid"] = u.Uuid, ["flow"] = "xtls-rprx-vision" });
                inbound["users"] = usersArray;
                UpdateVlessLinks(inbound, dbUsers, serverIp, isQuic);
            } else if (type == "hysteria2") UpdateHysteria2Links(inbound, dbUsers, serverIp);
        }

        if (!hasReality) foreach (var u in dbUsers) u.VlessLink = "VLESS не установлен!";
        if (!hasHy2) foreach (var u in dbUsers) u.Hysteria2Link = "Hysteria 2 не установлен!";
        if (!hasTrustTunnel) foreach (var u in dbUsers) u.TrustTunnelLink = "TrustTunnel не установлен!";
        await _dbContext.SaveChangesAsync();
    }

    private void UpdateVlessLinks(JsonObject inbound, List<VpnClient> dbUsers, string serverIp, bool isQuic)
    {
        string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
        string sni = inbound["tls"]?["server_name"]?.ToString() ?? "www.microsoft.com";
        int port = (int?)inbound["listen_port"] ?? (isQuic ? 4433 : 443);

        if (!isQuic) {
            string pubKey = "", shortId = "";
            try {
                var profile = _profileRepository.LoadProfiles().FirstOrDefault(p => p.IpAddress == serverIp);
                var settings = JsonDocument.Parse(profile?.Inbounds.FirstOrDefault(i => i.Protocol == "vless")?.SettingsJson ?? "{}").RootElement;
                pubKey = settings.GetProperty("publicKey").GetString() ?? ""; shortId = settings.GetProperty("shortId").GetString() ?? "";
            } catch { }
            foreach (var u in dbUsers) u.VlessLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=tcp&security=reality&pbk={pubKey}&fp=chrome&sni={sni}&sid={shortId}&spx=%2F&flow=xtls-rprx-vision#SingBox_{u.Email}";
        } else {
            foreach (var u in dbUsers) u.TrustTunnelLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=quic&security=tls&sni={sni}&allowInsecure=1&insecure=1#TrustTunnel_{u.Email}";
        }
    }

    private void UpdateHysteria2Links(JsonObject inbound, List<VpnClient> dbUsers, string serverIp)
    {
        string safeIp = serverIp.Contains(":") && !serverIp.StartsWith("[") ? $"[{serverIp}]" : serverIp;
        int port = (int?)inbound["listen_port"] ?? 8443;
        string sni = inbound["tls"]?["server_name"]?.ToString() ?? "www.microsoft.com";
        string pass = inbound["obfs"]?["password"]?.ToString() ?? "";
        string obfs = string.IsNullOrEmpty(pass) ? "" : $"&obfs=salamander&obfs-password={pass}";
        var users = new JsonArray();
        foreach (var u in dbUsers) {
            if (u.IsActive && u.IsHysteria2Enabled) users.Add(new JsonObject { ["name"] = u.Email, ["password"] = u.Uuid });
            u.Hysteria2Link = $"hy2://{u.Uuid}@{safeIp}:{port}?sni={sni}&insecure=1{obfs}#SingBox_HY2_{u.Email}";
        }
        inbound["users"] = users;
    }

    private async Task ApplyP2PRulesAsync(JsonNode root, string serverIp)
    {
        try
        {
            var blockedNames = await _dbContext.Clients.AsNoTracking().Where(c => c.ServerIp == serverIp && c.IsP2PBlocked).Select(c => c.Email.Trim()).ToListAsync();
            if (root["log"] is JsonObject logObj) logObj["level"] = "trace";
            else if (root.AsObject() != null) root["log"] = new JsonObject { ["level"] = "trace" };

            var inbounds = root?["inbounds"]?.AsArray();
            if (inbounds != null)
            {
                foreach (var inbound in inbounds)
                {
                    if (inbound is JsonObject inboundObj)
                    {
                        inboundObj.Remove("sniff"); inboundObj.Remove("sniffing"); inboundObj.Remove("sniff_override_destination");
                    }
                }
            }

            string rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules");
            if (!Directory.Exists(rulesDir)) Directory.CreateDirectory(rulesDir);
            string rulesFile = Path.Combine(rulesDir, "torrent_domains.txt");
            List<string> domains;

            if (!File.Exists(rulesFile))
            {
                domains = new List<string> { "torrent", "tracker", "rutracker", "nnmclub", "kinozal", "rutor", "piratebay", "tapochek", "lostfilm" };
                await File.WriteAllLinesAsync(rulesFile, domains);
            }
            else
            {
                domains = (await File.ReadAllLinesAsync(rulesFile)).Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim()).ToList();
            }

            var rulesArray = root?["route"]?["rules"]?.AsArray();
            if (rulesArray != null)
            {
                var rulesToRemove = rulesArray.Where(r => r?["outbound"]?.ToString() == "block" || r?["action"]?.ToString() == "sniff").ToList();
                foreach (var r in rulesToRemove) rulesArray.Remove(r);

                if (blockedNames.Any())
                {
                    rulesArray.Insert(0, new JsonObject { ["action"] = "sniff" });
                    if (domains.Any()) rulesArray.Insert(1, new JsonObject { ["auth_user"] = JsonSerializer.SerializeToNode(blockedNames), ["domain_keyword"] = JsonSerializer.SerializeToNode(domains), ["outbound"] = "block" });
                    rulesArray.Insert(2, new JsonObject { ["auth_user"] = JsonSerializer.SerializeToNode(blockedNames), ["protocol"] = "bittorrent", ["outbound"] = "block" });
                }
            }
        }
        catch { }
    }

    private async Task<(bool IsSuccess, string Message)> ApplyAndTestConfigAsync(ISshService ssh, string newJson)
    {
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(newJson.Replace("\r", "")));
        await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d > /tmp/sb_test.json");
        if ((await ssh.ExecuteCommandAsync("sing-box check -c /tmp/sb_test.json 2>&1")).Contains("error")) return (false, "Ошибка теста конфига!");
        await ssh.ExecuteCommandAsync("cp /etc/sing-box/config.json /etc/sing-box/config.backup.json; mv /tmp/sb_test.json /etc/sing-box/config.json; systemctl stop sing-box xray 2>/dev/null; killall -9 sing-box xray 2>/dev/null; systemctl restart sing-box");
        return (true, "Обновлено!");
    }
}
