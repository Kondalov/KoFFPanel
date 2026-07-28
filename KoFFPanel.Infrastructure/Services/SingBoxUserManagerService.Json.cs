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

        var profile = _profileRepository.LoadProfiles().FirstOrDefault(p => p.IpAddress == serverIp);
        string displayServer = !string.IsNullOrWhiteSpace(profile?.ConnectionNode) ? profile.ConnectionNode.Trim() : serverIp;

        foreach (var inbound in inbounds.OfType<JsonObject>().ToList())
        {
            var type = inbound["type"]?.ToString();
            var inboundDb = profile?.Inbounds.FirstOrDefault(i => i.Tag == inbound["tag"]?.ToString() || (i.Protocol == type && i.Port.ToString() == inbound["listen_port"]?.ToString()));
            var settingsDb = inboundDb != null ? JsonNode.Parse(inboundDb.SettingsJson) : null;

            if (type == "vless")
            {
                var isQuic = "quic".Equals(inbound["transport"]?["type"]?.ToString(), StringComparison.OrdinalIgnoreCase) ||
                             "xhttp".Equals(inbound["transport"]?["type"]?.ToString(), StringComparison.OrdinalIgnoreCase);

                // Синхронизация Reality ключей из БД в конфиг
                if (settingsDb != null && inbound["tls"]?["reality"] != null)
                {
                    inbound["tls"]!["reality"]!["private_key"] = settingsDb["privateKey"]?.ToString();
                    inbound["tls"]!["reality"]!["short_id"] = new JsonArray { settingsDb["shortId"]?.ToString() };
                    inbound["tls"]!["server_name"] = settingsDb["sni"]?.ToString() ?? "google.com";
                    inbound["tls"]!["reality"]!["handshake"]!["server"] = settingsDb["sni"]?.ToString() ?? "google.com";
                }

                var targetUsers = dbUsers.Where(u => u.IsActive && ((!isQuic && u.IsVlessEnabled) || (isQuic && u.IsTrustTunnelEnabled))).ToList();
                var usersArray = new JsonArray();

                if (targetUsers.Any())
                {
                    foreach (var u in targetUsers)
                        usersArray.Add(isQuic ? new JsonObject { ["name"] = u.Email, ["uuid"] = u.Uuid } : new JsonObject { ["name"] = u.Email, ["uuid"] = u.Uuid, ["flow"] = "xtls-rprx-vision" });
                }
                else usersArray.Add(new JsonObject { ["name"] = "init", ["uuid"] = "00000000-0000-0000-0000-000000000000" });

                inbound["users"] = usersArray;
                UpdateVlessLinks(inbound, dbUsers, displayServer, serverIp, isQuic);
            }
            else if (type == "hysteria2")
            {
                inbound["ignore_client_bandwidth"] = true;
                // Синхронизация Obfs пароля из БД в конфиг
                if (settingsDb != null && inbound["obfs"] != null)
                {
                    inbound["obfs"]!["password"] = settingsDb["obfsPassword"]?.ToString();
                    inbound["tls"]!["server_name"] = settingsDb["sni"]?.ToString() ?? "bing.com";
                }

                var targetUsers = dbUsers.Where(u => u.IsActive && u.IsHysteria2Enabled).ToList();
                var usersArray = new JsonArray();
                if (targetUsers.Any())
                {
                    foreach (var u in targetUsers) usersArray.Add(new JsonObject { ["name"] = u.Email, ["password"] = u.Uuid });
                }
                else usersArray.Add(new JsonObject { ["name"] = "init", ["password"] = "init_pass" });

                inbound["users"] = usersArray;
                UpdateHysteria2Links(inbound, dbUsers, displayServer);
            }
            else if (type == "trojan")
            {
                if (inbound["tls"] != null && inbound["tls"]!["alpn"] == null)
                {
                    inbound["tls"]!["alpn"] = new JsonArray { "h2", "http/1.1" };
                }
                // Синхронизация SNI из БД в конфиг
                if (settingsDb != null && inbound["tls"] != null)
                {
                    inbound["tls"]!["server_name"] = settingsDb["sni"]?.ToString() ?? "bing.com";
                }

                var targetUsers = dbUsers.Where(u => u.IsActive && u.IsTrojanEnabled).ToList();
                var usersArray = new JsonArray();
                if (targetUsers.Any())
                {
                    foreach (var u in targetUsers) usersArray.Add(new JsonObject { ["password"] = u.Uuid, ["name"] = u.Email });
                }
                else usersArray.Add(new JsonObject { ["password"] = "init_pass", ["name"] = "init" });

                inbound["users"] = usersArray;
                UpdateTrojanLinks(inbound, dbUsers, displayServer);
            }
            else if (type == "shadowsocks")
            {
                // Синхронизация метода из БД в конфиг
                if (settingsDb != null)
                {
                    inbound["method"] = settingsDb["method"]?.ToString() ?? "aes-256-gcm";
                }

                var targetUsers = dbUsers.Where(u => u.IsActive && u.IsShadowsocksEnabled).ToList();
                var usersArray = new JsonArray();
                if (targetUsers.Any())
                {
                    foreach (var u in targetUsers) usersArray.Add(new JsonObject { ["password"] = u.Uuid, ["name"] = u.Email });
                }
                else usersArray.Add(new JsonObject { ["password"] = "init_pass", ["name"] = "init" });

                inbound.Remove("password");
                inbound["users"] = usersArray;
                UpdateShadowsocksLinks(inbound, dbUsers, displayServer);
            }
        }

        if (root != null)
        {
            await ApplyP2PRulesAsync(root, serverIp);
        }
        await _dbContext.SaveChangesAsync();
    }

    private void UpdateTrojanLinks(JsonObject inbound, List<VpnClient> dbUsers, string displayServer)
    {
        string safeIp = displayServer.Contains(":") && !displayServer.StartsWith("[") ? $"[{displayServer}]" : displayServer;
        int port = 4434;
        if (inbound["listen_port"] != null) int.TryParse(inbound["listen_port"]!.ToString(), out port);
        string sni = inbound["tls"]?["server_name"]?.ToString() ?? "bing.com";

        foreach (var u in dbUsers)
        {
            string encodedName = Uri.EscapeDataString($"KoFF_{u.Email}");
            u.TrojanLink = $"trojan://{u.Uuid}@{safeIp}:{port}?security=tls&sni={sni}&type=tcp&alpn=h2&allowInsecure=1&insecure=1#{encodedName}";
        }
    }

    private void UpdateShadowsocksLinks(JsonObject inbound, List<VpnClient> dbUsers, string displayServer)
    {
        string safeIp = displayServer.Contains(":") && !displayServer.StartsWith("[") ? $"[{displayServer}]" : displayServer;
        int port = 8388;
        if (inbound["listen_port"] != null) int.TryParse(inbound["listen_port"]!.ToString(), out port);
        string method = inbound["method"]?.ToString() ?? "aes-256-gcm";

        foreach (var u in dbUsers)
        {
            string credentials = $"{method}:{u.Uuid}";
            
            // ИСПРАВЛЕНИЕ: Формат ss://base64(method:password)@host:port#name
            string base64Creds = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
            string encodedName = Uri.EscapeDataString($"KoFF_{u.Email}");
            
            u.ShadowsocksLink = $"ss://{base64Creds}@{safeIp}:{port}#{encodedName}";
        }
    }

    private void UpdateVlessLinks(JsonObject inbound, List<VpnClient> dbUsers, string displayServer, string serverIp, bool isQuic)
    {
        string safeIp = displayServer.Contains(":") && !displayServer.StartsWith("[") ? $"[{displayServer}]" : displayServer;
        string sni = inbound["tls"]?["server_name"]?.ToString() ?? "google.com";
        int port = isQuic ? 4433 : 443;
        if (inbound["listen_port"] != null) int.TryParse(inbound["listen_port"]!.ToString(), out port);

        if (!isQuic)
        {
            string pubKey = "", shortId = "";
            try
            {
                var profile = _profileRepository.LoadProfiles().FirstOrDefault(p => p.IpAddress == serverIp);
                var settings = JsonDocument.Parse(profile?.Inbounds.FirstOrDefault(i => i.Protocol == "vless")?.SettingsJson ?? "{}").RootElement;
                pubKey = settings.GetProperty("publicKey").GetString() ?? ""; shortId = settings.GetProperty("shortId").GetString() ?? "";
            }
            catch { }
            foreach (var u in dbUsers)
            {
                string encodedName = Uri.EscapeDataString($"SB_VLESS_{u.Email}");
                u.VlessLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=tcp&security=reality&pbk={pubKey}&fp=chrome&sni={sni}&sid={shortId}&spx=%2F&flow=xtls-rprx-vision&alpn=h2#{encodedName}";
            }
        }
        else
        {
            foreach (var u in dbUsers)
            {
                string encodedName = Uri.EscapeDataString($"TT_{u.Email}");
                u.TrustTunnelLink = $"vless://{u.Uuid}@{safeIp}:{port}?type=xhttp&security=tls&sni={sni.Replace("google.com", "vpn.endpoint")}&alpn=h3&allowInsecure=1&insecure=1#{encodedName}";
            }
        }
    }

    private void UpdateHysteria2Links(JsonObject inbound, List<VpnClient> dbUsers, string displayServer)
    {
        string safeIp = displayServer.Contains(":") && !displayServer.StartsWith("[") ? $"[{displayServer}]" : displayServer;
        int port = 8443;
        if (inbound["listen_port"] != null) int.TryParse(inbound["listen_port"]!.ToString(), out port);

        string sni = inbound["tls"]?["server_name"]?.ToString() ?? "bing.com";
        string pass = inbound["obfs"]?["password"]?.ToString() ?? "";
        string obfs = string.IsNullOrEmpty(pass) ? "" : $"&obfs=salamander&obfs-password={pass}";

        foreach (var u in dbUsers)
        {
            string encodedName = Uri.EscapeDataString($"SB_HY2_{u.Email}");
            u.Hysteria2Link = $"hy2://{u.Uuid}@{safeIp}:{port}?sni={sni}&insecure=1{obfs}&alpn=h3#{encodedName}";
        }
    }

    private async Task ApplyP2PRulesAsync(JsonNode root, string serverIp)
    {
        try
        {
            var blockedNames = await _dbContext.Clients.AsNoTracking().Where(c => c.ServerIp == serverIp && c.IsP2PBlocked).Select(c => c.Email.Trim()).ToListAsync();

            if (root["log"] is JsonObject logObj) logObj["level"] = "info";
            else if (root is JsonObject rootObj) rootObj["log"] = new JsonObject { ["level"] = "info" };

            bool isLegacyDns = false;
            if (root["dns"]?["servers"] is JsonArray srvArr && srvArr.Count > 0 && srvArr[0]?["address"] != null)
            {
                isLegacyDns = true;
            }

            if (root is JsonObject rootJObj && (rootJObj["dns"] == null || isLegacyDns))
            {
                rootJObj["dns"] = new JsonObject
                {
                    ["servers"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "https", ["tag"] = "remote", ["server"] = "8.8.8.8", ["domain_resolver"] = "local" },
                        new JsonObject { ["type"] = "udp", ["tag"] = "local", ["server"] = "8.8.8.8" }
                    },
                    ["rules"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["domain_suffix"] = new JsonArray { "openai.com", "chatgpt.com", "auth0.com", "google.com", "gemini.google.com", "googleapis.com", "generativelanguage.googleapis.com", "claude.ai", "anthropic.com" },
                            ["server"] = "remote"
                        }
                    },
                    ["final"] = "remote",
                    ["strategy"] = "prefer_ipv4"
                };
            }
            else if (root["dns"] is JsonObject dnsObj)
            {
                dnsObj["strategy"] = "prefer_ipv4";
            }

            if (root["route"] is JsonObject routeObj)
            {
                routeObj["default_domain_resolver"] = "local";
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

            var rulesArray = root["route"]?["rules"]?.AsArray();
            if (rulesArray != null)
            {
                var rulesToRemove = rulesArray.Where(r => r?["outbound"]?.ToString() == "block" || r?["action"]?.ToString() == "sniff").ToList();
                foreach (var r in rulesToRemove) rulesArray.Remove(r);

                // ВСЕГДА гарантируем работу sniffing для инспекции доменов и корректной работы AI
                rulesArray.Insert(0, new JsonObject { ["action"] = "sniff" });

                if (blockedNames.Any())
                {
                    if (domains.Any()) rulesArray.Add(new JsonObject { ["user"] = JsonSerializer.SerializeToNode(blockedNames), ["domain_keyword"] = JsonSerializer.SerializeToNode(domains), ["outbound"] = "block" });
                    rulesArray.Add(new JsonObject { ["user"] = JsonSerializer.SerializeToNode(blockedNames), ["protocol"] = "bittorrent", ["outbound"] = "block" });
                }
            }
        }
        catch { }
    }

    private async Task<(bool IsSuccess, string Message)> ApplyAndTestConfigAsync(ISshService ssh, string newJson)
    {
        string s = (await ssh.ExecuteCommandAsync("if [ \"$EUID\" -ne 0 ]; then echo 'sudo'; fi")).Trim();

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(newJson.Replace("\r", "")));
        await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d | {s} tee /tmp/sb_test.json >/dev/null");

        string checkCmd = $"{s} mkdir -p /var/log/sing-box && if [ -f /usr/local/bin/sing-box ]; then {s} /usr/local/bin/sing-box check -c /tmp/sb_test.json 2>&1; else {s} sing-box check -c /tmp/sb_test.json 2>&1; fi";
        string checkOutput = await ssh.ExecuteCommandAsync(checkCmd);
        if (checkOutput.Contains("error") || checkOutput.Contains("fatal"))
        {
            return (false, $"Ошибка теста конфига: {checkOutput}");
        }

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
                    var portToken = inbound["listen_port"] ?? inbound["port"];
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
                          $"{s} sysctl -w net.core.rmem_max=16777216 net.core.wmem_max=16777216 2>/dev/null || true; " +
                          $"{s} \\cp -f /etc/sing-box/config.json /etc/sing-box/config.backup.json; " +
                          $"{s} \\mv -f /tmp/sb_test.json /etc/sing-box/config.json; " +
                          $"{s} killall -HUP sing-box 2>/dev/null || {s} systemctl restart sing-box";

        await ssh.ExecuteCommandAsync(applyCmd);

        return (true, "Обновлено (Hot Reload)!");
    }
}
