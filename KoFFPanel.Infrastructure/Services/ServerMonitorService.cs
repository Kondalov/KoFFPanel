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
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (long Rx, long Tx, long Timestamp)> _previousNetStats = new();

    private static DatabaseReader? _cachedGeoReader;
    private static readonly object _geoLock = new();
    private static bool _geoLoadAttempted = false;

    private static DatabaseReader? GetSharedGeoReader(string dbPath)
    {
        if (_cachedGeoReader != null) return _cachedGeoReader;
        lock (_geoLock)
        {
            if (_cachedGeoReader != null || _geoLoadAttempted) return _cachedGeoReader;
            _geoLoadAttempted = true;
            if (File.Exists(dbPath))
            {
                try
                {
                    _cachedGeoReader = new DatabaseReader(dbPath, MaxMind.Db.FileAccessMode.Memory);
                }
                catch { }
            }
            return _cachedGeoReader;
        }
    }

    private string CalculateNetworkSpeed(string serverKey, long currentRx, long currentTx)
    {
        long now = Environment.TickCount64;
        if (_previousNetStats.TryGetValue(serverKey, out var prev) && prev.Timestamp > 0)
        {
            double seconds = (now - prev.Timestamp) / 1000.0;
            if (seconds > 0.5)
            {
                double rxDelta = Math.Max(0, currentRx - prev.Rx);
                double txDelta = Math.Max(0, currentTx - prev.Tx);
                double rxMbps = (rxDelta * 8.0) / (seconds * 1_000_000.0);
                double txMbps = (txDelta * 8.0) / (seconds * 1_000_000.0);
                _previousNetStats[serverKey] = (currentRx, currentTx, now);
                return $"↓{rxMbps:F2} ↑{txMbps:F2} Mbps";
            }
        }
        _previousNetStats[serverKey] = (currentRx, currentTx, now);
        return "↓0.00 ↑0.00 Mbps";
    }

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
            RX=$(cat /sys/class/net/$IFACE/statistics/rx_bytes 2>/dev/null || echo 0)
            TX=$(cat /sys/class/net/$IFACE/statistics/tx_bytes 2>/dev/null || echo 0)
            
            if [ ""$CORE"" = ""sing-box"" ]; then
                CORE_PROC=$(pgrep -f ""sing-box run"" -c || echo 0)
                ERR_TOTAL=$(journalctl -u sing-box -n 100 --no-pager 2>/dev/null | grep -ic ""error\|fatal\|rejected"")
            else
                CORE_PROC=$(pgrep -f ""xray run"" -c || echo 0)
                ERR_ACC=$(tail -n 100 /var/log/xray/access.log 2>/dev/null | grep -ic ""rejected"")
                ERR_ERR=$(tail -n 100 /var/log/xray/error.log 2>/dev/null | grep -ic ""error\|fail\|rejected"")
                ERR_TOTAL=$((ERR_ACC + ERR_ERR))
            fi
            
            TCP_CONN=$(ss -Htun state established 2>/dev/null | wc -l)
            SYN_RECV=$(ss -Ht state syn-recv 2>/dev/null | wc -l)

            echo ""${{CPU:-0}}|${{RAM:-0}}|${{DISK:-0}}|${{UPTIME:-N/A}}|${{LOADAVG:-0}}|${{RX}}|${{TX}}|${{CORE_PROC:-0}}|${{TCP_CONN:-0}}|${{SYN_RECV:-0}}|${{ERR_TOTAL:-0}}""
        ".Replace("\r", "");

        try
        {
            string result = await sshService.ExecuteCommandAsync(cmdText);
            string[] parts = result.Replace("\r", "").Replace("\n", "").Trim().Split('|');

            if (parts.Length == 11)
            {
                int.TryParse(parts[0], out int cpu);
                int.TryParse(parts[1], out int ram);
                int.TryParse(parts[2], out int ssd);
                long.TryParse(parts[5], out long rx);
                long.TryParse(parts[6], out long tx);
                int.TryParse(parts[7], out int coreProc);
                int.TryParse(parts[8], out int tcpConn);
                int.TryParse(parts[9], out int synRecv);
                int.TryParse(parts[10], out int errorRate);

                string speed = CalculateNetworkSpeed(coreType, rx, tx);
                return new ServerResources(cpu, ram, ssd, parts[3], parts[4], speed, coreProc, tcpConn, synRecv, errorRate);
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
            rawLogs = await sshService.ExecuteCommandAsync("([ -s /var/log/sing-box/access.log ] && tail -n 2000 /var/log/sing-box/access.log) || journalctl -u sing-box -n 2000 --no-pager 2>/dev/null | grep -iE 'inbound connection|remoteAddr'");
        }
        else
        {
            rawLogs = await sshService.ExecuteCommandAsync("tail -n 2000 /var/log/xray/access.log 2>/dev/null | grep 'accepted'");
        }

        if (string.IsNullOrWhiteSpace(rawLogs)) return stats;

        // Данные по пользователю: (последний IP, количество активных сессий)
        var userStats = new Dictionary<string, (string LastIp, int ActiveSessions)>(StringComparer.OrdinalIgnoreCase);
        var lines = rawLogs.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);

        if (coreType.ToLower() == "sing-box")
        {
            var connIdToIp = new Dictionary<string, string>();
            var connIdToPort = new Dictionary<string, string>();
            var connIdToProto = new Dictionary<string, string>();
            var connIdToUser = new Dictionary<string, string>();
            var connIdToTime = new Dictionary<string, DateTime>();
            DateTime? maxLogTime = null;

            foreach (var line in lines)
            {
                try
                {
                    string cleanLine = line;
                    int sbPrefixIdx = cleanLine.IndexOf("sing-box[");
                    if (sbPrefixIdx != -1)
                    {
                        int closeBracket = cleanLine.IndexOf("]:", sbPrefixIdx);
                        if (closeBracket != -1)
                        {
                            cleanLine = cleanLine.Substring(closeBracket + 2);
                        }
                    }

                    // Выцепляем ID соединения из лога (напр. [1984081120 71ms] -> 1984081120)
                    var idMatch = System.Text.RegularExpressions.Regex.Match(cleanLine, @"\[(\d+)(?:\s+[^\]]+)?\]");
                    string connId = idMatch.Success ? idMatch.Groups[1].Value : "";

                    if (string.IsNullOrEmpty(connId)) continue;

                    // Timestamp (напр. 2026-09-16 13:17:49)
                    var timeMatch = System.Text.RegularExpressions.Regex.Match(cleanLine, @"(\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}:\d{2})");
                    if (timeMatch.Success && DateTime.TryParse(timeMatch.Groups[1].Value, out var dt))
                    {
                        connIdToTime[connId] = dt;
                        if (!maxLogTime.HasValue || dt > maxLogTime.Value)
                            maxLogTime = dt;
                    }

                    // Протокол/инбаунд
                    var protoMatch = System.Text.RegularExpressions.Regex.Match(cleanLine, @"inbound/(\w+)\[");
                    if (protoMatch.Success)
                    {
                        connIdToProto[connId] = protoMatch.Groups[1].Value.ToLowerInvariant();
                    }

                    // ИЩЕМ IP и порт
                    string rawAddr = "";
                    if (cleanLine.Contains("remoteAddr:"))
                    {
                        var ipMatch = System.Text.RegularExpressions.Regex.Match(cleanLine, @"remoteAddr:\s*([0-9a-fA-F\.\:\[\]]+)");
                        if (ipMatch.Success) rawAddr = ipMatch.Groups[1].Value.Trim();
                    }
                    else if (cleanLine.Contains("inbound connection from"))
                    {
                        var ipMatch = System.Text.RegularExpressions.Regex.Match(cleanLine, @"inbound connection from\s*([0-9a-fA-F\.\:\[\]]+)");
                        if (ipMatch.Success) rawAddr = ipMatch.Groups[1].Value.Trim();
                    }

                    if (!string.IsNullOrEmpty(rawAddr))
                    {
                        string ip = rawAddr;
                        string port = "";
                        if (ip.StartsWith("[") && ip.Contains("]"))
                        {
                            int close = ip.IndexOf(']');
                            string rest = ip.Substring(close + 1);
                            ip = ip.Substring(1, close - 1);
                            if (rest.StartsWith(":")) port = rest.Substring(1);
                        }
                        else if (ip.Contains(':'))
                        {
                            int lastColon = ip.LastIndexOf(':');
                            port = ip.Substring(lastColon + 1);
                            ip = ip.Substring(0, lastColon);
                        }

                        connIdToIp[connId] = ip.Replace("[", "").Replace("]", "");
                        if (!string.IsNullOrEmpty(port)) connIdToPort[connId] = port;
                    }

                    // ИЩЕМ ЮЗЕРА
                    var userMatch = System.Text.RegularExpressions.Regex.Match(cleanLine, @"\]:\s*\[(.*?)\]\s*inbound connection");
                    if (userMatch.Success)
                    {
                        connIdToUser[connId] = userMatch.Groups[1].Value.Trim();
                    }
                }
                catch { }
            }

            var userConnections = new Dictionary<string, List<(DateTime? Time, string Proto, string Ip, string Port)>>(StringComparer.OrdinalIgnoreCase);

            foreach (var kvp in connIdToUser)
            {
                string connId = kvp.Key;
                string user = kvp.Value;

                if (!connIdToIp.TryGetValue(connId, out string? ip) || string.IsNullOrEmpty(ip))
                    continue;

                connIdToPort.TryGetValue(connId, out string? port);
                connIdToProto.TryGetValue(connId, out string? proto);
                connIdToTime.TryGetValue(connId, out DateTime connTime);

                DateTime? time = connTime != default ? connTime : null;
                proto ??= "unknown";
                port ??= "";

                if (!userConnections.ContainsKey(user))
                    userConnections[user] = new List<(DateTime? Time, string Proto, string Ip, string Port)>();

                userConnections[user].Add((time, proto, ip, port));
            }

            foreach (var kvp in userConnections)
            {
                string user = kvp.Key;
                var conns = kvp.Value;

                var latestConn = conns.OrderByDescending(c => c.Time ?? DateTime.MinValue).FirstOrDefault();
                string lastIp = latestConn.Ip ?? conns.Last().Ip;

                var activeSessionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in conns)
                {
                    bool isActive = true;
                    if (maxLogTime.HasValue && c.Time.HasValue)
                    {
                        isActive = (maxLogTime.Value - c.Time.Value).TotalSeconds <= 180;
                    }

                    if (isActive)
                    {
                        // Для UDP-протоколов (Hysteria2, TUIC) отдельный локальный порт = отдельное устройство
                        // Для TCP-протоколов (VLESS, Trojan) группируем по протокол:IP во избежание раздувания от коротких сокетов
                        string sessionKey = (c.Proto == "hysteria2" || c.Proto == "tuic") && !string.IsNullOrEmpty(c.Port)
                            ? $"{c.Proto}:{c.Ip}:{c.Port}"
                            : $"{c.Proto}:{c.Ip}";
                        activeSessionKeys.Add(sessionKey);
                    }
                }

                int activeSessions = activeSessionKeys.Count;
                if (activeSessions == 0 && conns.Count > 0)
                    activeSessions = 1;

                userStats[user] = (lastIp, activeSessions);
            }
        }
        else
        {
            var userConnections = new Dictionary<string, List<(DateTime? Time, string Tag, string Ip, string Port)>>(StringComparer.OrdinalIgnoreCase);
            DateTime? maxLogTime = null;

            foreach (var line in lines)
            {
                try
                {
                    string user = "Unknown";
                    string ip = "";
                    string port = "";
                    string tag = "xray";
                    DateTime? time = null;

                    var timeMatch = System.Text.RegularExpressions.Regex.Match(line, @"(\d{4}/\d{2}/\d{2}\s+\d{2}:\d{2}:\d{2})");
                    if (timeMatch.Success && DateTime.TryParse(timeMatch.Groups[1].Value.Replace('/', '-'), out var dt))
                    {
                        time = dt;
                        if (!maxLogTime.HasValue || dt > maxLogTime.Value)
                            maxLogTime = dt;
                    }

                    var tagMatch = System.Text.RegularExpressions.Regex.Match(line, @"\[(.*?)\]");
                    if (tagMatch.Success) tag = tagMatch.Groups[1].Value.Trim().ToLowerInvariant();

                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    for (int i = 0; i < parts.Length; i++)
                    {
                        if (parts[i].StartsWith("email:")) user = parts[i].Replace("email:", "").Trim();
                        else if (parts[i].StartsWith("[") && parts[i].EndsWith("]"))
                        {
                            string pot = parts[i].Trim('[', ']');
                            if (!string.IsNullOrEmpty(pot) && pot != tag) user = pot;
                        }

                        if (parts[i] == "accepted" && i > 0)
                        {
                            string rawAddr = parts[i - 1].Replace("tcp:", "").Replace("udp:", "").Trim();
                            if (rawAddr.Contains(':'))
                            {
                                var addrParts = rawAddr.Split(':');
                                ip = addrParts[0];
                                if (addrParts.Length > 1) port = addrParts[1];
                            }
                            else
                            {
                                ip = rawAddr;
                            }
                        }
                    }

                    if (user != "Unknown" && !string.IsNullOrEmpty(ip))
                    {
                        if (!userConnections.ContainsKey(user))
                            userConnections[user] = new List<(DateTime? Time, string Tag, string Ip, string Port)>();

                        userConnections[user].Add((time, tag, ip, port));
                    }
                }
                catch { }
            }

            foreach (var kvp in userConnections)
            {
                string user = kvp.Key;
                var conns = kvp.Value;

                var latestConn = conns.OrderByDescending(c => c.Time ?? DateTime.MinValue).FirstOrDefault();
                string lastIp = latestConn.Ip ?? conns.Last().Ip;

                var activeSessionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var c in conns)
                {
                    bool isActive = true;
                    if (maxLogTime.HasValue && c.Time.HasValue)
                    {
                        isActive = (maxLogTime.Value - c.Time.Value).TotalSeconds <= 180;
                    }

                    if (isActive)
                    {
                        activeSessionKeys.Add($"{c.Tag}:{c.Ip}");
                    }
                }

                int activeSessions = activeSessionKeys.Count;
                if (activeSessions == 0 && conns.Count > 0)
                    activeSessions = 1;

                userStats[user] = (lastIp, activeSessions);
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

        DatabaseReader? geoReader = GetSharedGeoReader(dbPath);
        bool hasGeoDb = geoReader != null;
        string dbError = hasGeoDb ? "" : "No DB";

        try
        {
            foreach (var kvp in userStats)
            {
                string lastIp = kvp.Value.LastIp?.Trim() ?? "";
                int activeSessions = kvp.Value.ActiveSessions;
                string country = dbError != "" ? dbError : "??";

                if (hasGeoDb && geoReader != null && !string.IsNullOrEmpty(lastIp))
                {
                    try
                    {
                        string cleanIp = lastIp.Trim();
                        if (cleanIp.StartsWith("[") && cleanIp.Contains("]"))
                        {
                            int closeBracket = cleanIp.IndexOf(']');
                            cleanIp = cleanIp.Substring(1, closeBracket - 1);
                        }
                        else if (cleanIp.Contains(':') && cleanIp.IndexOf(':') == cleanIp.LastIndexOf(':'))
                        {
                            cleanIp = cleanIp.Split(':')[0];
                        }

                        if (System.Net.IPAddress.TryParse(cleanIp, out var parsedIp))
                        {
                            if (geoReader.TryCountry(parsedIp, out var response))
                            {
                                if (!string.IsNullOrEmpty(response?.Country?.IsoCode))
                                {
                                    country = response.Country.IsoCode.ToUpperInvariant();
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
                    ActiveSessions = activeSessions,
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

    public static string FormatCountryWithFlag(string? isoCode)
    {
        if (string.IsNullOrWhiteSpace(isoCode) || isoCode == "??")
            return "??";

        return isoCode.Trim().ToUpperInvariant();
    }

    public async Task<(bool Success, long RoundtripTime)> PingServerAsync(string ip, int timeoutMs = 2000)
    {
        // 1. Стандартный ICMP Ping (Идеально работает до включения ключа)
        try
        {
            using var pinger = new Ping();
            var reply = await pinger.SendPingAsync(ip, timeoutMs);
            // Foolproof защита: реальный удаленный сервер не ответит быстрее 2 мс. 
            // Всё что быстрее — это фальшивый ответ от локального TUN-адаптера.
            if (reply.Status == IPStatus.Success && reply.RoundtripTime > 2)
            {
                return (true, reply.RoundtripTime);
            }
        }
        catch { }

        // 2. Бронебойный TCP Payload Ping (Обходит Fake Handshake)
        // Мы отправляем минимальный HTTP-запрос на быстрые порты и ждем ответ.
        // Nginx (80), Python (8080) и Xray (443) обрабатывают это мгновенно (< 1 мс),
        // поэтому замеряется ИСКЛЮЧИТЕЛЬНО сетевое время (твои 40-70 ms).
        int[] fastPorts = { 80, 8080, 443 };

        foreach (var port in fastPorts)
        {
            var tcpClient = new System.Net.Sockets.TcpClient();
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var connectTask = tcpClient.ConnectAsync(ip, port);

                if (await Task.WhenAny(connectTask, Task.Delay(timeoutMs)) == connectTask)
                {
                    await connectTask; // Убеждаемся, что нет исключений подключения

                    using var stream = tcpClient.GetStream();

                    // Отправляем легчайший HTTP HEAD запрос
                    byte[] request = System.Text.Encoding.ASCII.GetBytes($"HEAD / HTTP/1.1\r\nHost: {ip}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(request, 0, request.Length);

                    // Ждем реакции сервера (ответ или разрыв соединения)
                    byte[] buffer = new byte[1];
                    var readTask = stream.ReadAsync(buffer, 0, 1);

                    if (await Task.WhenAny(readTask, Task.Delay(timeoutMs)) == readTask)
                    {
                        // Независимо от того, ответил сервер (bytes > 0) или сбросил соединение
                        // (bytes == 0, как делает Xray Reality на 443 порту при мусорном трафике),
                        // это 100% честный сетевой Roundtrip.
                        sw.Stop();
                        long ping = sw.ElapsedMilliseconds;

                        // Защита от фальшивых локальных ответов
                        if (ping > 2)
                        {
                            return (true, ping);
                        }
                    }
                }
            }
            catch { }
            finally
            {
                // Жесткое закрытие сокета для предотвращения утечек в ОС
                try { tcpClient.Close(); } catch { }
            }
        }

        return (false, 0);
    }

    /// <summary>
    /// метод выполняет комплексную проверку статуса ядра (Xray/Sing-Box) через SSH, собирая данные о версии, валидности конфига, аптайме и последних ошибках. Вся логика обработки и вычислений вынесена в единый shell-скрипт для минимизации количества SSH вызовов и обеспечения максимальной точности данных, особенно в условиях LXC/OpenVZ, где системные часы могут быть рассинхронизированы. Результат возвращается в виде объекта CoreStatusInfo, который может быть легко отображен в UI или сохранен в аналитике.
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