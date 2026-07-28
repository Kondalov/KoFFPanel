using KoFFPanel.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public partial class XrayUserManagerService
{
    private async Task RebuildInboundsAsync(JsonNode root, string serverIp, ISshService ssh)
    {
        var dbUsers = await _dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
        var inbounds = root["inbounds"]?.AsArray();
        if (inbounds == null) return;

        var profile = _profileRepository.LoadProfiles().FirstOrDefault(p => p.IpAddress == serverIp);
        string displayServer = !string.IsNullOrWhiteSpace(profile?.ConnectionNode) ? profile.ConnectionNode.Trim() : serverIp;

        foreach (var inbound in inbounds.OfType<JsonObject>().ToList())
        {
            string protocol = inbound["protocol"]?.ToString() ?? "";
            var net = inbound["streamSettings"]?["network"]?.ToString();
            bool isXHttp = "xhttp".Equals(net, StringComparison.OrdinalIgnoreCase) || "quic".Equals(net, StringComparison.OrdinalIgnoreCase);

            if ("vless".Equals(protocol, StringComparison.OrdinalIgnoreCase))
            {
                var targetUsers = dbUsers.Where(u => u.IsActive && (!isXHttp ? u.IsVlessEnabled : u.IsTrustTunnelEnabled)).ToList();
                var clients = new JsonArray();

                if (targetUsers.Any())
                {
                    foreach (var u in targetUsers)
                    {
                        var clientObj = new JsonObject { ["id"] = u.Uuid, ["email"] = u.Email };
                        if (!isXHttp) clientObj["flow"] = "xtls-rprx-vision";
                        clients.Add(clientObj);
                    }
                }
                else
                {
                    clients.Add(new JsonObject { ["id"] = "00000000-0000-0000-0000-000000000000", ["email"] = "init" });
                }

                if (inbound["settings"] is JsonObject s) s["clients"] = clients;
                await UpdateXrayVlessLinksAsync(inbound, dbUsers, displayServer, ssh, isXHttp);
            }
            else if ("trojan".Equals(protocol, StringComparison.OrdinalIgnoreCase))
            {
                var targetUsers = dbUsers.Where(u => u.IsActive && u.IsTrojanEnabled).ToList();
                var clients = new JsonArray();

                if (targetUsers.Any())
                {
                    foreach (var u in targetUsers)
                        clients.Add(new JsonObject { ["password"] = u.Uuid, ["email"] = u.Email });
                }
                else
                {
                    clients.Add(new JsonObject { ["password"] = "init_pass", ["email"] = "init" });
                }

                if (inbound["settings"] is JsonObject s) s["clients"] = clients;
                UpdateTrojanLinks(inbound, dbUsers, displayServer);
            }
            else if ("shadowsocks".Equals(protocol, StringComparison.OrdinalIgnoreCase))
            {
                var targetUsers = dbUsers.Where(u => u.IsActive && u.IsShadowsocksEnabled).ToList();
                var clients = new JsonArray();

                if (targetUsers.Any())
                {
                    foreach (var u in targetUsers)
                        clients.Add(new JsonObject { ["password"] = u.Uuid, ["email"] = u.Email });
                }
                else clients.Add(new JsonObject { ["password"] = "init_pass", ["email"] = "init" });

                if (inbound["settings"] is JsonObject s) s["clients"] = clients;

                // ИСПРАВЛЕНИЕ: Удалена ошибочная вставка streamSettings (WebSocket) для Shadowsocks в Xray
                UpdateShadowsocksLinks(inbound, dbUsers, displayServer);
            }
        }

        await ApplyP2PRulesAsync(root, dbUsers);
        await _dbContext.SaveChangesAsync();
    }

    private void UpdateTrojanLinks(JsonObject inbound, List<KoFFPanel.Domain.Entities.VpnClient> dbUsers, string displayServer)
    {
        string safeIp = displayServer.Contains(":") && !displayServer.StartsWith("[") ? $"[{displayServer}]" : displayServer;
        int port = 4434;
        if (inbound["port"] != null) int.TryParse(inbound["port"]!.ToString(), out port);
        string sni = inbound["streamSettings"]?["tlsSettings"]?["serverName"]?.ToString() ?? "bing.com";

        foreach (var u in dbUsers)
        {
            string encodedName = Uri.EscapeDataString($"KoFF_{u.Email}");
            // ИСПРАВЛЕНИЕ: Добавлен insecure=1
            u.TrojanLink = $"trojan://{u.Uuid}@{safeIp}:{port}?security=tls&sni={sni}&type=tcp&alpn=http/1.1,h2&allowInsecure=1&insecure=1#{encodedName}";
        }
    }

    private void UpdateShadowsocksLinks(JsonObject inbound, List<KoFFPanel.Domain.Entities.VpnClient> dbUsers, string displayServer)
    {
        string safeIp = displayServer.Contains(":") && !displayServer.StartsWith("[") ? $"[{displayServer}]" : displayServer;
        int port = 8388;
        if (inbound["port"] != null) int.TryParse(inbound["port"]!.ToString(), out port);
        string method = inbound["settings"]?["method"]?.ToString() ?? "aes-256-gcm";

        foreach (var u in dbUsers)
        {
            string credentials = $"{method}:{u.Uuid}";

            string base64Creds = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials))
                .Replace("+", "-")
                .Replace("/", "_")
                .TrimEnd('=');

            string encodedName = Uri.EscapeDataString($"KoFF_{u.Email}");
            u.ShadowsocksLink = $"ss://{base64Creds}@{safeIp}:{port}#{encodedName}";
        }
    }

    private async Task UpdateXrayVlessLinksAsync(JsonObject inbound, List<KoFFPanel.Domain.Entities.VpnClient> dbUsers, string displayServer, ISshService ssh, bool isQuic)
    {
        int port = isQuic ? 4433 : 443;
        if (inbound["port"] != null) int.TryParse(inbound["port"]!.ToString(), out port);

        string safeIp = displayServer.Contains(':') && !displayServer.StartsWith('[') ? $"[{displayServer}]" : displayServer;

        if (!isQuic)
        {
            var rs = inbound["streamSettings"]?["realitySettings"];
            string sid = rs?["shortIds"]?[0]?.ToString() ?? "";
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
                // ИСПРАВЛЕНО: заменено {shortId} на {sid}
                u.VlessLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=tcp&security=reality&pbk={pub}&fp=chrome&sni={sni}&sid={sid}&spx=%2F&flow=xtls-rprx-vision&alpn=h2#{encodedName}";
            }
        }
    }

    private async Task ApplyP2PRulesAsync(JsonNode root, List<KoFFPanel.Domain.Entities.VpnClient> dbUsers)
    {
        var rules = root["routing"]?["rules"]?.AsArray();
        if (rules == null) return;

        var toRemove = rules.Where(r => r?["outboundTag"]?.ToString() == "block" && (r?["protocol"]?.ToString().Contains("bittorrent") == true || r?["domain"] != null)).ToList();
        foreach (var r in toRemove) rules.Remove(r);

        var blockedUsers = dbUsers.Where(u => u.IsP2PBlocked).Select(u => u.Email).ToList();
        if (blockedUsers.Any())
        {
            var userArray = new JsonArray();
            foreach (var email in blockedUsers) userArray.Add(email);

            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["user"] = userArray.DeepClone(),
                ["protocol"] = new JsonArray("bittorrent"),
                ["outboundTag"] = "block"
            });

            rules.Add(new JsonObject
            {
                ["type"] = "field",
                ["user"] = userArray.DeepClone(),
                ["domain"] = new JsonArray("domain:nnmclub.to", "domain:rutracker.org", "domain:rutor.info", "domain:kinozal.tv", "domain:tapochek.net", "keyword:torrent"),
                ["outboundTag"] = "block"
            });
        }
    }

    private async Task<(bool IsSuccess, string Message)> ApplyAndTestConfigAsync(ISshService ssh, string newJson)
    {
        string s = (await ssh.ExecuteCommandAsync("if [ \"$EUID\" -ne 0 ]; then echo 'sudo'; fi")).Trim();

        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(newJson.Replace("\r", "", StringComparison.OrdinalIgnoreCase)));
        await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d | {s} tee /tmp/config_users_test.json >/dev/null");

        string testResult = await ssh.ExecuteCommandAsync("/usr/local/bin/xray run -test -config /tmp/config_users_test.json 2>&1");

        if (!testResult.Contains("Configuration OK", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Ошибка теста Xray! Конфиг не прошел валидацию.");
        }

        // ИСПРАВЛЕНИЕ: Автоматический парсинг портов и открытие Firewall
        var ports = new HashSet<int>();
        try
        {
            var parsed = JsonNode.Parse(newJson);
            var inbounds = parsed?["inbounds"]?.AsArray();
            if (inbounds != null)
            {
                foreach (var inbound in inbounds)
                {
                    if (inbound == null) continue;
                    var portToken = inbound["port"] ?? inbound["listen_port"];
                    if (portToken != null && int.TryParse(portToken.ToString(), out int p)) ports.Add(p);
                }
            }
        }
        catch { }

        var sbFw = new StringBuilder();
        foreach (var p in ports)
        {
            sbFw.Append($"{s} ufw allow {p}/tcp 2>/dev/null; {s} ufw allow {p}/udp 2>/dev/null; {s} iptables -I INPUT -p tcp --dport {p} -j ACCEPT 2>/dev/null; {s} iptables -I INPUT -p udp --dport {p} -j ACCEPT 2>/dev/null; ");
        }
        string fwCmds = sbFw.ToString();

        string applyCmd = $"{fwCmds} " +
                          $"{s} \\cp -f /usr/local/etc/xray/config.json /usr/local/etc/xray/config.backup.json; " +
                          $"{s} \\mv -f /tmp/config_users_test.json /usr/local/etc/xray/config.json; " +
                          $"{s} systemctl restart xray";

        await ssh.ExecuteCommandAsync(applyCmd);

        return (true, "Обновлено!");
    }
}