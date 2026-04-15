using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using MaxMind.GeoIP2;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class ServerMonitorService : IServerMonitorService
{
    public async Task<(int Cpu, int Ram, int Ssd, string Uptime, string LoadAvg, string NetworkSpeed, int XrayProcesses, int TcpConnections, int SynRecv, int ErrorRate)> GetResourcesAsync(ISshService sshService, string coreType)
    {
        if (!sshService.IsConnected) return (0, 0, 0, "N/A", "0.0", "0 Mbps", 0, 0, 0, 0);

        string cmdText = $@"
            export PATH=$PATH:/usr/local/bin:/usr/bin:/bin:/sbin:/usr/sbin
            CORE=""{coreType.ToLower()}""

            CPU=$(top -bn1 2>/dev/null | grep -i '%Cpu' | head -n 1 | awk '{{print $2+$4}}' | cut -d. -f1 | cut -d, -f1)
            RAM=$(free -m | awk 'NR==2{{printf ""%.0f"", $3*100/$2}}')
            DISK=$(df -h / | awk '$NF==""/""{{print $5}}' | tr -d '%')
            
            UP_SEC=$(cat /proc/uptime 2>/dev/null | awk -F. '{{print $1}}')
            UPTIME=$(awk -v t=""${{UP_SEC:-0}}"" 'BEGIN {{printf ""%dd %dh %dm"", t/86400, (t%86400)/3600, (t%3600)/60}}')
            
            LOADAVG=$(cat /proc/loadavg 2>/dev/null | awk '{{print $1}}')
            
            IFACE=$(ip route 2>/dev/null | grep default | awk '{{print $5}}' | head -n1)
            RX1=$(cat /sys/class/net/$IFACE/statistics/rx_bytes 2>/dev/null || echo 0)
            TX1=$(cat /sys/class/net/$IFACE/statistics/tx_bytes 2>/dev/null || echo 0)
            sleep 1
            RX2=$(cat /sys/class/net/$IFACE/statistics/rx_bytes 2>/dev/null || echo 0)
            TX2=$(cat /sys/class/net/$IFACE/statistics/tx_bytes 2>/dev/null || echo 0)
            
            RX_MBPS=$(awk -v r1=""$RX1"" -v r2=""$RX2"" 'BEGIN {{ printf ""%.2f"", (r2-r1)*8/1000000 }}')
            TX_MBPS=$(awk -v t1=""$TX1"" -v t2=""$TX2"" 'BEGIN {{ printf ""%.2f"", (t2-t1)*8/1000000 }}')
            
            if [ ""$CORE"" = ""sing-box"" ]; then
                CORE_PROC=$(pgrep -f ""sing-box run"" -c || echo 0)
                ERR_TOTAL=$(journalctl -u sing-box --since ""10 minutes ago"" --no-pager 2>/dev/null | grep -ic ""error\|fatal\|rejected"")
            elif [ ""$CORE"" = ""trusttunnel"" ]; then
                CORE_PROC=$(pgrep -f ""trusttunnel"" -c || echo 0)
                ERR_TOTAL=$(journalctl -u trusttunnel --since ""10 minutes ago"" --no-pager 2>/dev/null | grep -ic ""error\|fatal\|panic"")
            else
                CORE_PROC=$(pgrep -f ""xray run"" -c || echo 0)
                ERR_ACC=$(tail -n 1000 /var/log/xray/access.log 2>/dev/null | grep -ic ""rejected"")
                ERR_ERR=$(tail -n 1000 /var/log/xray/error.log 2>/dev/null | grep -ic ""error\|fail\|rejected"")
                ERR_TOTAL=$((ERR_ACC + ERR_ERR))
            fi
            
            TCP_CONN=$(ss -Htun state established 2>/dev/null | wc -l)
            SYN_RECV=$(ss -Ht state syn-recv 2>/dev/null | wc -l)

            echo ""${{CPU:-0}}|${{RAM:-0}}|${{DISK:-0}}|${{UPTIME:-N/A}}|${{LOADAVG:-0}}|↓${{RX_MBPS}} ↑${{TX_MBPS}} Mbps|${{CORE_PROC:-0}}|${{TCP_CONN:-0}}|${{SYN_RECV:-0}}|${{ERR_TOTAL:-0}}""
        ".Replace("\r", ""); // ИСПРАВЛЕНИЕ: ЖЕСТКО ВЫРЕЗАЕМ \r ДЛЯ LINUX BASH!

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

    public async Task<List<UserOnlineInfo>> GetUserOnlineStatsAsync(ISshService sshService, string coreType)
    {
        var stats = new List<UserOnlineInfo>();
        if (!sshService.IsConnected) return stats;

        string rawLogs = "";
        if (coreType.ToLower() == "sing-box")
        {
            rawLogs = await sshService.ExecuteCommandAsync("journalctl -u sing-box -n 2000 --no-pager 2>/dev/null | grep -E 'inbound connection from|inbound connection to'");
        }
        else if (coreType.ToLower() == "trusttunnel")
        {
            rawLogs = await sshService.ExecuteCommandAsync("journalctl -u trusttunnel -n 2000 --no-pager 2>/dev/null");
        }
        else
        {
            rawLogs = await sshService.ExecuteCommandAsync("tail -n 2000 /var/log/xray/access.log 2>/dev/null | grep 'accepted'");
        }

        if (string.IsNullOrWhiteSpace(rawLogs)) return stats;

        var userIps = new Dictionary<string, HashSet<string>>();
        var lines = rawLogs.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        // ИСПРАВЛЕНИЕ: Словарь для склейки разорванных логов Sing-box по ID соединения!
        var connIdToIp = new Dictionary<string, string>();

        foreach (var line in lines)
        {
            try
            {
                if (coreType.ToLower() == "sing-box")
                {
                    string connId = "";
                    int idStart = line.IndexOf("] [");
                    if (idStart != -1)
                    {
                        int idEnd = line.IndexOf(' ', idStart + 3);
                        if (idEnd != -1) connId = line.Substring(idStart + 3, idEnd - idStart - 3);
                    }

                    if (line.Contains("inbound connection from "))
                    {
                        var parts = line.Split(new[] { "inbound connection from " }, StringSplitOptions.None);
                        if (parts.Length > 1 && !string.IsNullOrEmpty(connId))
                        {
                            string ipPort = parts[1].Trim();
                            string ip = ipPort;
                            if (ipPort.StartsWith("["))
                            {
                                int endBracket = ipPort.IndexOf(']');
                                if (endBracket != -1) ip = ipPort.Substring(1, endBracket - 1);
                            }
                            else
                            {
                                ip = ipPort.Split(':')[0];
                            }
                            connIdToIp[connId] = ip;
                        }
                    }
                    else if (line.Contains("] inbound connection to "))
                    {
                        string user = "Unknown";
                        int userStart = line.IndexOf("]: [");
                        if (userStart != -1)
                        {
                            int userEnd = line.IndexOf("] inbound", userStart);
                            if (userEnd != -1) user = line.Substring(userStart + 4, userEnd - userStart - 4);
                        }

                        if (user != "Unknown" && !string.IsNullOrEmpty(connId) && connIdToIp.ContainsKey(connId))
                        {
                            string ip = connIdToIp[connId];
                            if (!userIps.ContainsKey(user)) userIps[user] = new HashSet<string>();
                            userIps[user].Add(ip);
                        }
                    }
                }
                else
                {
                    string user = "Unknown";
                    string ip = "";
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i].StartsWith("email:")) user = parts[i].Replace("email:", "").Trim();
                        else if (parts[i].StartsWith("[") && parts[i].EndsWith("]")) user = parts[i].Trim('[', ']');

                        if (parts[i] == "accepted" && i > 0)
                        {
                            ip = parts[i - 1].Split(':')[0].Replace("tcp:", "").Replace("udp:", "").Trim();
                        }
                    }

                    if (user != "Unknown" && !string.IsNullOrEmpty(ip))
                    {
                        if (!userIps.ContainsKey(user)) userIps[user] = new HashSet<string>();
                        userIps[user].Add(ip);
                    }
                }
            }
            catch { }
        }

        string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeoLite2-Country.mmdb");
        if (!File.Exists(dbPath))
        {
            var currentDir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            while (currentDir != null)
            {
                string checkPath = Path.Combine(currentDir.FullName, "GeoLite2-Country.mmdb");
                if (File.Exists(checkPath))
                {
                    dbPath = checkPath;
                    break;
                }
                currentDir = currentDir.Parent;
            }
        }

        bool hasGeoDb = File.Exists(dbPath);
        DatabaseReader? geoReader = null;
        if (hasGeoDb)
        {
            try { geoReader = new DatabaseReader(dbPath); }
            catch { hasGeoDb = false; }
        }

        try
        {
            foreach (var kvp in userIps)
            {
                string lastIp = kvp.Value.LastOrDefault()?.Trim() ?? "";
                string country = "??";

                if (hasGeoDb && geoReader != null && !string.IsNullOrEmpty(lastIp))
                {
                    try
                    {
                        string cleanIp = lastIp.Contains(":") && !lastIp.Contains("]") ? lastIp.Split(':')[0] : lastIp;
                        cleanIp = cleanIp.Replace("[", "").Replace("]", "").Trim();

                        if (System.Net.IPAddress.TryParse(cleanIp, out var parsedIp))
                        {
                            if (geoReader.TryCountry(parsedIp, out var response))
                            {
                                if (!string.IsNullOrEmpty(response?.Country?.IsoCode))
                                {
                                    country = response.Country.IsoCode;
                                }
                            }
                        }
                    }
                    catch { }
                }

                stats.Add(new UserOnlineInfo
                {
                    Email = kvp.Key,
                    LastIp = lastIp,
                    ActiveSessions = kvp.Value.Count,
                    Country = country == "??" ? "??" : $"🌍 {country}"
                });
            }
        }
        finally
        {
            geoReader?.Dispose();
        }

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

    public async Task<CoreStatusInfo> GetCoreStatusInfoAsync(ISshService sshService, string coreType)
    {
        var info = new CoreStatusInfo();
        if (!sshService.IsConnected) return info;

        string cmdText = $@"
        export PATH=$PATH:/usr/local/bin:/usr/bin:/bin:/sbin:/usr/sbin
        CORE=""{coreType.ToLower()}""
        NOW=$(date +%s)
        
        BIN=$(command -v $CORE 2>/dev/null || echo ""/usr/local/bin/$CORE"")

        if [ ""$CORE"" = ""sing-box"" ]; then
            V=$($BIN version 2>/dev/null | grep 'version' | awk '{{print $3}}')
            $BIN check -c /etc/sing-box/config.json >/dev/null 2>&1 && C=""Валиден"" || C=""Ошибка""
            PID=$(systemctl show -p ExecMainPID sing-box 2>/dev/null | awk -F= '{{print $2}}')
            E=$(journalctl -u sing-box -n 50 --no-pager 2>/dev/null | grep -iE 'error|fatal|rejected' | tail -n 1 | sed 's/.*msg=//' | tr -d '\r\n')
        elif [ ""$CORE"" = ""trusttunnel"" ]; then
            V=$($BIN --version 2>/dev/null | awk '{{print $2}}')
            [ -f /etc/trusttunnel/vpn.toml ] && C=""Валиден"" || C=""Ошибка""
            PID=$(systemctl show -p ExecMainPID trusttunnel 2>/dev/null | awk -F= '{{print $2}}')
            E=$(journalctl -u trusttunnel -n 50 --no-pager 2>/dev/null | grep -iE 'error|fatal|panic' | tail -n 1 | tr -d '\r\n')
        else
            V=$($BIN version 2>/dev/null | head -n 1 | awk '{{print $2}}')
            if [ -z ""$V"" ]; then V=$($BIN -version 2>/dev/null | head -n 1 | awk '{{print $2}}'); fi
            $BIN run -test -config /usr/local/etc/xray/config.json >/dev/null 2>&1 && C=""Валиден"" || C=""Ошибка""
            PID=$(systemctl show -p ExecMainPID xray 2>/dev/null | awk -F= '{{print $2}}')
            E=$(tail -n 200 /var/log/xray/error.log 2>/dev/null | grep -iE 'error|fail|rejected' | grep -v '\[Info\]' | tail -n 1 | tr -d '\r\n')
        fi

        if [ -n ""$PID"" ] && [ ""$PID"" != ""0"" ]; then
            START=$(stat -c %Y /proc/$PID 2>/dev/null || echo $NOW)
            ELAPSED=$((NOW - START))
            U=$(awk -v t=""$ELAPSED"" 'BEGIN {{printf ""%dd %dh %dm"", t/86400, (t%86400)/3600, (t%3600)/60}}')
        else
            U=""Остановлен""
        fi

        V=${{V:-""Неизвестно""}}
        [ -z ""$C"" ] && C=""Неизвестно""
        [ -z ""$E"" ] && E=""Нет ошибок""
        echo ""$V|$C|$U|$E""
        ".Replace("\r", "");

        try
        {
            string result = await sshService.ExecuteCommandAsync(cmdText);
            var parts = result.Replace("\r", "").Split('\n').LastOrDefault(s => s.Contains('|'))?.Split('|');

            if (parts != null && parts.Length >= 4)
            {
                info.Version = parts[0].Trim();
                info.ConfigStatus = parts[1].Trim();
                info.Uptime = parts[2].Trim();

                string err = parts[3].Trim();
                // ИСПРАВЛЕНИЕ: Обрезаем длинный текст ошибки, чтобы он не ломал UI!
                info.LastError = err.Length > 35 ? err.Substring(0, 35) + "..." : err;
            }
        }
        catch { }

        return info;
    }
}