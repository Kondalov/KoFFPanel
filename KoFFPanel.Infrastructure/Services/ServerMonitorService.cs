using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class ServerMonitorService : IServerMonitorService
{
    public async Task<(int Cpu, int Ram, int Ssd, string Uptime, string LoadAvg, string NetworkSpeed, int XrayProcesses, int TcpConnections, int SynRecv, int ErrorRate)> GetResourcesAsync(ISshService sshService)
    {
        if (!sshService.IsConnected) return (0, 0, 0, "N/A", "0.0", "0 Mbps", 0, 0, 0, 0);

        string cmdText = @"
            CORE=""xray""
            systemctl is-active --quiet sing-box && CORE=""sing-box""

            CPU=$(top -bn1 | grep -i '%Cpu' | head -n 1 | awk '{print $2+$4}' | cut -d. -f1 | cut -d, -f1)
            RAM=$(free -m | awk 'NR==2{printf ""%.0f"", $3*100/$2}')
            DISK=$(df -h / | awk '$NF==""/""{print $5}' | tr -d '%')
            
            UP_SEC=$(cat /proc/uptime | awk -F. '{print $1}')
            UPTIME=$(awk -v t=""$UP_SEC"" 'BEGIN {printf ""%dd %dh %dm"", t/86400, (t%86400)/3600, (t%3600)/60}')
            
            LOADAVG=$(cat /proc/loadavg | awk '{print $1}')
            
            IFACE=$(ip route | grep default | awk '{print $5}' | head -n1)
            RX1=$(cat /sys/class/net/$IFACE/statistics/rx_bytes 2>/dev/null || echo 0)
            TX1=$(cat /sys/class/net/$IFACE/statistics/tx_bytes 2>/dev/null || echo 0)
            sleep 1
            RX2=$(cat /sys/class/net/$IFACE/statistics/rx_bytes 2>/dev/null || echo 0)
            TX2=$(cat /sys/class/net/$IFACE/statistics/tx_bytes 2>/dev/null || echo 0)
            
            RX_MBPS=$(awk -v r1=""$RX1"" -v r2=""$RX2"" 'BEGIN { printf ""%.2f"", (r2-r1)*8/1000000 }')
            TX_MBPS=$(awk -v t1=""$TX1"" -v t2=""$TX2"" 'BEGIN { printf ""%.2f"", (t2-t1)*8/1000000 }')
            
            if [ ""$CORE"" = ""sing-box"" ]; then
                CORE_PROC=$(pgrep -c sing-box || echo 0)
                ERR_TOTAL=$(journalctl -u sing-box --since ""10 minutes ago"" --no-pager | grep -ic ""error\|fatal\|rejected"")
            else
                CORE_PROC=$(pgrep -c xray || echo 0)
                ERR_ACC=$(tail -n 1000 /var/log/xray/access.log 2>/dev/null | grep -ic ""rejected"")
                ERR_ERR=$(tail -n 1000 /var/log/xray/error.log 2>/dev/null | grep -ic ""error\|fail\|rejected"")
                ERR_TOTAL=$((ERR_ACC + ERR_ERR))
            fi
            
            TCP_CONN=$(ss -Htun state established 2>/dev/null | wc -l)
            SYN_RECV=$(ss -Ht state syn-recv 2>/dev/null | wc -l)

            echo ""${CPU:-0}|${RAM:-0}|${DISK:-0}|${UPTIME:-N/A}|${LOADAVG:-0}|↓${RX_MBPS} ↑${TX_MBPS} Mbps|${CORE_PROC:-0}|${TCP_CONN:-0}|${SYN_RECV:-0}|${ERR_TOTAL:-0}""
        ";

        try
        {
            string result = await sshService.ExecuteCommandAsync(cmdText);
            string[] parts = result.Replace("\r", "").Replace("\n", "").Trim().Split('|');

            if (parts.Length == 10)
            {
                int.TryParse(parts[0], out int cpu);
                int.TryParse(parts[1], out int ram);
                int.TryParse(parts[2], out int ssd);
                int.TryParse(parts[6], out int coreProc);
                int.TryParse(parts[7], out int tcpConn);
                int.TryParse(parts[8], out int synRecv);
                int.TryParse(parts[9], out int errorRate);

                return (cpu, ram, ssd, parts[3], parts[4], parts[5], coreProc, tcpConn, synRecv, errorRate);
            }
        }
        catch { }

        return (0, 0, 0, "N/A", "0.0", "0 Mbps", 0, 0, 0, 0);
    }

    public async Task<List<UserOnlineInfo>> GetUserOnlineStatsAsync(ISshService sshService)
    {
        var stats = new List<UserOnlineInfo>();
        if (!sshService.IsConnected) return stats;

        string coreCheck = await sshService.ExecuteCommandAsync("systemctl is-active --quiet sing-box && echo 'sing-box' || echo 'xray'");
        bool isSingBox = coreCheck.Trim() == "sing-box";

        string rawLogs = "";

        // ОПТИМИЗАЦИЯ: Мы режем мусор прямо на сервере командой grep! Это исключает таймауты в 15 секунд.
        if (isSingBox)
        {
            rawLogs = await sshService.ExecuteCommandAsync("journalctl -u sing-box -n 2000 --no-pager | grep -E 'inbound connection from|inbound connection to'");
        }
        else
        {
            rawLogs = await sshService.ExecuteCommandAsync("tail -n 2000 /var/log/xray/access.log 2>/dev/null | grep 'accepted'");
        }

        if (string.IsNullOrWhiteSpace(rawLogs)) return stats;

        var lines = rawLogs.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        var userIps = new Dictionary<string, HashSet<string>>();

        // БРОНЕБОЙНЫЙ ПАРСЕР НА ЧИСТОМ C#
        if (isSingBox)
        {
            var idToIp = new Dictionary<string, string>();
            foreach (var line in lines)
            {
                try
                {
                    if (!line.Contains("INFO [")) continue;

                    int idStart = line.IndexOf("INFO [") + 6;
                    int idEnd = line.IndexOf(' ', idStart);
                    if (idStart < 6 || idEnd <= idStart) continue;
                    string id = line.Substring(idStart, idEnd - idStart);

                    if (line.Contains("inbound connection from"))
                    {
                        int ipStart = line.IndexOf("from ") + 5;
                        int ipEnd = line.IndexOf(':', ipStart);
                        if (ipEnd == -1) ipEnd = line.Length;
                        if (ipStart >= 5 && ipEnd > ipStart)
                        {
                            string ip = line.Substring(ipStart, ipEnd - ipStart).Trim();
                            idToIp[id] = ip;
                        }
                    }
                    else if (line.Contains("inbound connection to"))
                    {
                        int userStart = line.IndexOf("]: [");
                        if (userStart != -1)
                        {
                            userStart += 4;
                            int userEnd = line.IndexOf(']', userStart);
                            if (userEnd > userStart)
                            {
                                string user = line.Substring(userStart, userEnd - userStart).Trim();

                                if (idToIp.TryGetValue(id, out string ip))
                                {
                                    if (!userIps.ContainsKey(user)) userIps[user] = new HashSet<string>();
                                    userIps[user].Add(ip);
                                }
                            }
                        }
                    }
                }
                catch { /* Игнорируем битые строки лога */ }
            }
        }
        else
        {
            foreach (var line in lines)
            {
                try
                {
                    if (line.Contains("accepted") && line.Contains("email:"))
                    {
                        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        string ip = "";
                        string email = "";

                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (parts[i] == "accepted" && i >= 1) ip = parts[i - 1].Split(':')[0];
                            if (parts[i] == "email:" && i + 1 < parts.Length) email = parts[i + 1].Replace("]", "").Trim();
                        }

                        if (!string.IsNullOrEmpty(ip) && !string.IsNullOrEmpty(email))
                        {
                            if (!userIps.ContainsKey(email)) userIps[email] = new HashSet<string>();
                            userIps[email].Add(ip);
                        }
                    }
                }
                catch { /* Игнорируем битые строки лога */ }
            }
        }

        // ГЕОЛОКАЦИЯ
        string dbPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeoLite2-Country.mmdb");
        MaxMind.GeoIP2.DatabaseReader? reader = null;
        if (System.IO.File.Exists(dbPath)) reader = new MaxMind.GeoIP2.DatabaseReader(dbPath);

        foreach (var kvp in userIps)
        {
            string ip = kvp.Value.First();
            string country = "??";
            string flag = "🌍";

            if (reader != null && ip != "0.0.0.0")
            {
                try
                {
                    var response = reader.Country(ip);
                    string isoCode = response.Country.IsoCode ?? "";
                    if (isoCode.Length == 2)
                    {
                        country = isoCode;
                        int charA = char.ToUpper(isoCode[0]) + 0x1F1A5;
                        int charB = char.ToUpper(isoCode[1]) + 0x1F1A5;
                        flag = char.ConvertFromUtf32(charA) + char.ConvertFromUtf32(charB);
                    }
                }
                catch { }
            }

            stats.Add(new UserOnlineInfo { Email = kvp.Key, LastIp = ip, ActiveSessions = kvp.Value.Count, Country = $"{flag} {country}" });
        }
        reader?.Dispose();

        return stats;
    }

    public async Task<(bool Success, long RoundtripTime)> PingServerAsync(string ip, int timeoutMs = 2000)
    {
        try
        {
            using var pinger = new Ping();
            var reply = await pinger.SendPingAsync(ip, timeoutMs);
            if (reply.Status == IPStatus.Success) return (true, reply.RoundtripTime);
        }
        catch { }
        return (false, 0);
    }

    public async Task<CoreStatusInfo> GetCoreStatusInfoAsync(ISshService sshService)
    {
        var info = new CoreStatusInfo();
        if (!sshService.IsConnected) return info;

        string cmdText = @"
        CORE=""xray""; systemctl is-active --quiet sing-box && CORE=""sing-box""
        if [ ""$CORE"" = ""sing-box"" ]; then
            V=$(/usr/local/bin/sing-box version 2>/dev/null | grep 'version' | awk '{print $3}')
            /usr/local/bin/sing-box check -c /etc/sing-box/config.json >/dev/null 2>&1 && C=""Валиден"" || C=""Ошибка""
            P=$(pgrep -x sing-box | head -n 1)
            if [ -n ""$P"" ]; then
                M=$(ps -p $P -o rss= | awk '{printf ""%.1f"", $1/1024}')
                U=$(ps -p $P -o etimes= | awk '{printf ""%dd %dh %dm"", $1/86400, ($1%86400)/3600, ($1%3600)/60}')
            else
                M=""0.0""; U=""Остановлен""
            fi
            E=$(journalctl -u sing-box -n 50 --no-pager | grep -iE 'error|fatal|rejected' | tail -n 1 | sed 's/.*msg=//' | tr -d '\r\n')
        else
            V=$(/usr/local/bin/xray -version 2>/dev/null | head -n 1 | awk '{print $2}')
            /usr/local/bin/xray run -test -config /usr/local/etc/xray/config.json >/dev/null 2>&1 && C=""Валиден"" || C=""Ошибка""
            P=$(pgrep -x xray | head -n 1)
            if [ -n ""$P"" ]; then
                M=$(ps -p $P -o rss= | awk '{printf ""%.1f"", $1/1024}')
                U=$(ps -p $P -o etimes= | awk '{printf ""%dd %dh %dm"", $1/86400, ($1%86400)/3600, ($1%3600)/60}')
            else
                M=""0.0""; U=""Остановлен""
            fi
            E=$(tail -n 200 /var/log/xray/error.log 2>/dev/null | grep -iE 'error|fail|rejected' | grep -v '\[Info\]' | tail -n 1 | tr -d '\r\n')
        fi
        V=${V:-""Неизвестно""}
        [ -z ""$E"" ] && E=""Нет ошибок""
        echo ""$V|$C|$U|$M MB|$E""
        ";

        try
        {
            string result = await sshService.ExecuteCommandAsync(cmdText);
            var parts = result.Replace("\r", "").Split('\n').LastOrDefault(s => s.Contains('|'))?.Split('|');

            if (parts != null && parts.Length >= 5)
            {
                info.Version = parts[0].Trim();
                info.ConfigStatus = parts[1].Trim();
                info.Uptime = parts[2].Trim();
                info.MemoryUsage = parts[3].Trim();
                info.LastError = parts[4].Trim();
            }
        }
        catch { }

        return info;
    }
}