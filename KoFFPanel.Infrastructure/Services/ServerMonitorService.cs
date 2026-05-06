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
    private readonly IAppLogger _logger;
    public async Task<ServerResources> GetResourcesAsync(ISshService sshService, string coreType)
    {
        if (!sshService.IsConnected) return new ServerResources(0, 0, 0, "N/A", "0.0", "0 Mbps", 0, 0, 0, 0);

        string cmdText = $@"
            export LC_ALL=C
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
        ".Replace("\r", "");

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

                return new ServerResources(cpu, ram, ssd, parts[3], parts[4], parts[5], coreProc, tcpConn, synRecv, errorRate);
            }
        }
        catch { }

        return new ServerResources(0, 0, 0, "N/A", "0.0", "0 Mbps", 0, 0, 0, 0);
    }


    /// <summary>
    /// скрипт собирает значения абсолютно всех переменных (PID, SYS_UP, T_MONO и т.д.) и передает их в слой C#, который сохраняет их в app_analytics.log
    /// </summary>
    /// <param name="logger"></param>
    public ServerMonitorService(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<List<UserOnlineInfo>> GetUserOnlineStatsAsync(ISshService sshService, string coreType)
    {
        var stats = new List<UserOnlineInfo>();
        if (!sshService.IsConnected) return stats;

        string rawLogs = "";
        if (coreType.ToLower() == "sing-box")
        {
            // Читаем логи из файла, а если пуст - резервно из journalctl
            rawLogs = await sshService.ExecuteCommandAsync("tail -n 2000 /var/log/sing-box/access.log 2>/dev/null | grep -iE 'inbound connection|remoteAddr'");
            if (string.IsNullOrWhiteSpace(rawLogs))
            {
                rawLogs = await sshService.ExecuteCommandAsync("journalctl -u sing-box -n 2000 --no-pager 2>/dev/null | grep -iE 'inbound connection|remoteAddr'");
            }
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

        if (coreType.ToLower() == "sing-box")
        {
            var connIdToIp = new Dictionary<string, string>();
            var connIdToUser = new Dictionary<string, string>();

            foreach (var line in lines)
            {
                try
                {
                    // Выцепляем ID соединения из лога (напр. [1984081120 71ms] -> 1984081120)
                    var idMatch = System.Text.RegularExpressions.Regex.Match(line, @"\[(\d+)(?:\s+[^\]]+)?\]");
                    string connId = idMatch.Success ? idMatch.Groups[1].Value : "";

                    if (string.IsNullOrEmpty(connId)) continue;

                    // ИЩЕМ IP
                    if (line.Contains("remoteAddr:"))
                    {
                        var ipMatch = System.Text.RegularExpressions.Regex.Match(line, @"remoteAddr:\s*([0-9a-fA-F\.\:\[\]]+)");
                        if (ipMatch.Success)
                        {
                            string ip = ipMatch.Groups[1].Value.Trim();
                            ip = ip.Contains(":") && !ip.StartsWith("[") ? ip.Split(':')[0] : ip;
                            connIdToIp[connId] = ip.Replace("[", "").Replace("]", "");
                        }
                    }
                    else if (line.Contains("inbound connection from"))
                    {
                        var ipMatch = System.Text.RegularExpressions.Regex.Match(line, @"inbound connection from\s*([0-9a-fA-F\.\:\[\]]+)");
                        if (ipMatch.Success)
                        {
                            string ip = ipMatch.Groups[1].Value.Trim();
                            ip = ip.Contains(":") && !ip.StartsWith("[") ? ip.Split(':')[0] : ip;
                            connIdToIp[connId] = ip.Replace("[", "").Replace("]", "");
                        }
                    }

                    // ИЩЕМ ЮЗЕРА
                    var userMatch = System.Text.RegularExpressions.Regex.Match(line, @"\]:\s*\[(.*?)\]\s*inbound connection");
                    if (userMatch.Success)
                    {
                        connIdToUser[connId] = userMatch.Groups[1].Value.Trim();
                    }
                }
                catch { }
            }

            // Маппинг: Объединяем IP и Пользователя по общему ID соединения
            foreach (var kvp in connIdToUser)
            {
                string connId = kvp.Key;
                string user = kvp.Value;

                if (!userIps.ContainsKey(user)) userIps[user] = new HashSet<string>();

                if (connIdToIp.ContainsKey(connId))
                {
                    userIps[user].Add(connIdToIp[connId]);
                }
            }
        }
        else
        {
            // Старая логика Xray остается без изменений
            foreach (var line in lines)
            {
                try
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
                catch { }
            }
        }

        // Блок загрузки базы локаций GeoIP (без изменений)
        string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GeoLite2-Country.mmdb");
        if (!File.Exists(dbPath))
        {
            string fallbackPath = Path.Combine(Directory.GetCurrentDirectory(), "GeoLite2-Country.mmdb");
            if (File.Exists(fallbackPath))
            {
                dbPath = fallbackPath;
            }
            else
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
        }

        bool hasGeoDb = File.Exists(dbPath);
        DatabaseReader? geoReader = null;
        string dbError = "";

        if (hasGeoDb)
        {
            try
            {
                geoReader = new DatabaseReader(dbPath, MaxMind.Db.FileAccessMode.Memory);
            }
            catch (Exception)
            {
                hasGeoDb = false;
                dbError = "DB Err";
            }
        }
        else
        {
            dbError = "No DB";
        }

        try
        {
            foreach (var kvp in userIps)
            {
                string lastIp = kvp.Value.LastOrDefault()?.Trim() ?? "";
                string country = dbError != "" ? dbError : "??";

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
                    catch
                    {
                        country = "??";
                    }
                }

                stats.Add(new UserOnlineInfo
                {
                    Email = kvp.Key,
                    LastIp = lastIp,
                    ActiveSessions = kvp.Value.Count,
                    Country = country
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
        // 1. Стандартный ICMP Ping
        try
        {
            using var pinger = new Ping();
            var reply = await pinger.SendPingAsync(ip, timeoutMs);
            // Foolproof защита: реальный удаленный сервер не ответит быстрее 2 мс. 
            // Все что быстрее — это фальшивый ответ от локального TUN-адаптера.
            if (reply.Status == IPStatus.Success && reply.RoundtripTime > 2)
            {
                return (true, reply.RoundtripTime);
            }
        }
        catch { }

        // 2. HTTP L7 Ping с жестким обходом прокси
        // Это современный паттерн обхода маршрутизации VPN-клиентов в .NET
        try
        {
            using var handler = new System.Net.Http.HttpClientHandler
            {
                UseProxy = false, // СТРОГО: Игнорируем локальный прокси от VPN
                ServerCertificateCustomValidationCallback = (sender, cert, chain, errors) => true
            };

            using var client = new System.Net.Http.HttpClient(handler);
            client.Timeout = TimeSpan.FromMilliseconds(timeoutMs);
            client.DefaultRequestHeaders.ConnectionClose = true;

            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Обращаемся к нашему микросервису подписок
            var response = await client.GetAsync($"http://{ip}:8080/ping");

            if (response.IsSuccessStatusCode)
            {
                sw.Stop();
                long ping = sw.ElapsedMilliseconds;
                if (ping > 2) return (true, ping);
            }
        }
        catch { }

        // 3. Бронебойный TCP Ping через Socket
        int[] fallbackPorts = { 22, 443, 80 };
        foreach (var port in fallbackPorts)
        {
            try
            {
                using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
                socket.Blocking = false;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                using var cts = new System.Threading.CancellationTokenSource(timeoutMs);

                await socket.ConnectAsync(ip, port, cts.Token);
                if (socket.Connected)
                {
                    sw.Stop();
                    long ping = sw.ElapsedMilliseconds;
                    if (ping > 2) return (true, ping);
                }
            }
            catch { }
        }

        return (false, 0);
    }

    /// <summary>
    /// метод выполняет комплексную проверку статуса ядра (Xray/Sing-Box/TrustTunnel) через SSH, собирая данные о версии, валидности конфига, аптайме и последних ошибках. Вся логика обработки и вычислений вынесена в единый shell-скрипт для минимизации количества SSH вызовов и обеспечения максимальной точности данных, особенно в условиях LXC/OpenVZ, где системные часы могут быть рассинхронизированы. Результат возвращается в виде объекта CoreStatusInfo, который может быть легко отображен в UI или сохранен в аналитике.
    /// </summary>
    /// <param name="sshService"></param>
    /// <param name="coreType"></param>
    /// <returns></returns>
    public async Task<CoreStatusInfo> GetCoreStatusInfoAsync(ISshService sshService, string coreType)
    {
        var info = new CoreStatusInfo();
        if (!sshService.IsConnected) return info;

        // ВНЕДРЕНО: Аптайм и статус конфига удалены (срезаны под корень).
        // Скрипт теперь максимально легкий и запрашивает только Версию и Ошибки логов.
        string cmdText = $@"
        export LC_ALL=C
        export PATH=$PATH:/usr/local/bin:/usr/bin:/bin:/sbin:/usr/sbin
        
        CORE=""{coreType.ToLower()}""
        SVC=$CORE
        BIN=$(command -v $CORE 2>/dev/null || echo ""/usr/local/bin/$CORE"")

        if [ ""$CORE"" = ""sing-box"" ]; then
            V=$($BIN version 2>/dev/null | grep 'version' | awk '{{print $3}}')
            E=$(journalctl -u $SVC -n 10 --no-pager 2>/dev/null | grep -iE 'error|fatal|panic|rejected' | tail -n 1 | sed 's/.*msg=//' | tr -d '\r\n|')
        elif [ ""$CORE"" = ""trusttunnel"" ]; then
            V=$($BIN --version 2>/dev/null | awk '{{print $2}}')
            E=$(journalctl -u $SVC -n 10 --no-pager 2>/dev/null | grep -iE 'error|fatal|panic' | tail -n 1 | tr -d '\r\n|')
        else
            V=$($BIN version 2>/dev/null | head -n 1 | awk '{{print $2}}')
            if [ -z ""$V"" ]; then V=$($BIN -version 2>/dev/null | head -n 1 | awk '{{print $2}}'); fi
            E=$(journalctl -u $SVC -n 10 --no-pager 2>/dev/null | grep -iE 'error|fail|rejected' | grep -v '\[Info\]' | tail -n 1 | tr -d '\r\n|')
        fi

        echo ""${{V:-Неизвестно}}|${{E:-Нет ошибок}}""
        ".Replace("\r", "");

        try
        {
            string result = await sshService.ExecuteCommandAsync(cmdText);

            if (string.IsNullOrWhiteSpace(result))
            {
                info.LastError = "Таймаут SSH";
                return info;
            }

            var parts = result.Replace("\r", "").Split('\n').LastOrDefault(s => s.Contains('|'))?.Split('|');

            if (parts != null && parts.Length >= 2)
            {
                info.Version = parts[0].Trim();
                string err = parts[1].Trim();
                info.LastError = err.Length > 35 ? err.Substring(0, 35) + "..." : err;
            }
        }
        catch (Exception ex)
        {
            info.LastError = "КРАШ C#: " + ex.Message;
        }

        return info;
    }
}