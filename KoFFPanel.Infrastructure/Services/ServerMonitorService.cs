using KoFFPanel.Application.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class ServerMonitorService : IServerMonitorService
{
    public async Task<(int Cpu, int Ram, int Ssd, string Uptime, string LoadAvg, string NetworkSpeed, int XrayProcesses, int TcpConnections, int SynRecv)> GetResourcesAsync(ISshService sshService)
    {
        if (!sshService.IsConnected) return (0, 0, 0, "N/A", "0.0", "0 Mbps", 0, 0, 0);

        string cmdText = @"
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
            
            XRAY_PROC=$(pgrep -c xray || echo 0)
            
            TCP_CONN=$(ss -Htun state established 2>/dev/null | wc -l)
            SYN_RECV=$(ss -Ht state syn-recv 2>/dev/null | wc -l)

            echo ""${CPU:-0}|${RAM:-0}|${DISK:-0}|${UPTIME:-N/A}|${LOADAVG:-0}|↓${RX_MBPS} ↑${TX_MBPS} Mbps|${XRAY_PROC:-0}|${TCP_CONN:-0}|${SYN_RECV:-0}""
        ";

        try
        {
            string result = await sshService.ExecuteCommandAsync(cmdText);
            string cleanResult = result.Replace("\r", "").Replace("\n", "").Trim();
            string[] parts = cleanResult.Split('|');

            if (parts.Length == 9)
            {
                int.TryParse(parts[0], out int cpu);
                int.TryParse(parts[1], out int ram);
                int.TryParse(parts[2], out int ssd);
                string uptime = parts[3];
                string loadAvg = parts[4];
                string network = parts[5];
                int.TryParse(parts[6], out int xrayProc);
                int.TryParse(parts[7], out int tcpConn);
                int.TryParse(parts[8], out int synRecv);

                return (cpu, ram, ssd, uptime, loadAvg, network, xrayProc, tcpConn, synRecv);
            }
        }
        catch { }

        return (0, 0, 0, "N/A", "0.0", "0 Mbps", 0, 0, 0);
    }

    public async Task<List<UserOnlineInfo>> GetUserOnlineStatsAsync(ISshService sshService)
    {
        var stats = new List<UserOnlineInfo>();
        if (!sshService.IsConnected) return stats;

        // ПАРСЕР 7.0 (Однострочный):
        // Убираем многострочность (@""), так как Windows \r\n ломает bash-скрипты при передаче по SSH.
        // Выполняем 100% надежную однострочную команду.
        string logCmd = "tail -n 1000 /var/log/xray/access.log 2>/dev/null | awk '/accepted/ && /email:/ { ip=$4; sub(/:[0-9]+$/, \"\", ip); email=$NF; print email \"|\" ip; }' | sort | uniq";

        try
        {
            string result = await sshService.ExecuteCommandAsync(logCmd);
            var lines = result.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

            var userIps = new Dictionary<string, HashSet<string>>();

            foreach (var line in lines)
            {
                var parts = line.Split('|');
                if (parts.Length >= 2)
                {
                    // Жесткая очистка от скобок
                    var email = parts[0].Replace("]", "").Replace("[", "").Trim();
                    var ip = parts[1].Trim();

                    if (!userIps.ContainsKey(email))
                        userIps[email] = new HashSet<string>();

                    userIps[email].Add(ip);
                }
            }

            foreach (var kvp in userIps)
            {
                stats.Add(new UserOnlineInfo
                {
                    Email = kvp.Key,
                    LastIp = kvp.Value.First(),
                    ActiveSessions = kvp.Value.Count
                });
            }
        }
        catch { }
        return stats;
    }

    public async Task<(bool Success, long RoundtripTime)> PingServerAsync(string ip, int timeoutMs = 2000)
    {
        try
        {
            using var pinger = new Ping();
            var reply = await pinger.SendPingAsync(ip, timeoutMs);
            if (reply.Status == IPStatus.Success)
            {
                return (true, reply.RoundtripTime);
            }
        }
        catch { }

        return (false, 0);
    }
}