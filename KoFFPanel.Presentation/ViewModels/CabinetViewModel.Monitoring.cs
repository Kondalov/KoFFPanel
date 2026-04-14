using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace KoFFPanel.Presentation.ViewModels;

public partial class CabinetViewModel
{
    [ObservableProperty]
    private string _coreTitleLabel = "Ядро (Ожидание)";

    private long _previousTotalServerBytes = 0;

    private async Task StartMonitoringLoopAsync(VpnProfile profile, CancellationToken token)
    {
        IsMonitoringActive = true; ServerStatus = "Подключение...";
        ISshService localSsh = _sshServiceFactory();
        _currentMonitoringSsh = localSsh;

        string ip = profile.IpAddress ?? "";
        string user = profile.Username ?? "root";
        string pass = profile.Password ?? "";
        string key = profile.KeyPath ?? "";

        bool isSingBox = profile.CoreType == "sing-box";
        string displayCoreName = isSingBox ? "Sing-box" : "Xray-core";
        string serviceName = isSingBox ? "sing-box" : "xray";

        _logger.Log("MONITORING", $"[START] Запуск цикла мониторинга. Ядро по БД: {displayCoreName.ToUpper()}");

        if (await localSsh.ConnectAsync(ip, profile.Port, user, pass, key) != "SUCCESS")
        {
            ServerStatus = "Ошибка подключения";
            if (_currentMonitoringSsh == localSsh) IsMonitoringActive = false;
            localSsh.Disconnect(); return;
        }

        ServerStatus = "Онлайн (Сбор данных)";
        await LoadUsersAsync();

        _ = _analyticsService.CleanupOldLogsAsync();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var pingResult = await _monitorService.PingServerAsync(ip); PingMs = pingResult.Success ? pingResult.RoundtripTime : 0;

                var res = await _monitorService.GetResourcesAsync(localSsh);
                NetworkSpeed = res.NetworkSpeed; XrayProcesses = res.XrayProcesses; SynRecv = res.SynRecv; ErrorRate = res.ErrorRate;

                try
                {
                    string bashCmd = "cpu=$(top -bn1 2>/dev/null | grep -Ei 'Cpu\\(s\\)' | awk '{print $2+$4}' | cut -d. -f1 | tr -d '\n'); " +
                                     "if [ -z \"$cpu\" ]; then cpu=$(vmstat 1 2 2>/dev/null | tail -1 | awk '{print 100 - $15}'); fi; " +
                                     "ram=$(free | awk '/Mem:/ {printf(\"%d\", $3/$2 * 100)}' 2>/dev/null | tr -d '\n'); " +
                                     "ssd=$(df / | awk 'NR==2 {print $5}' | sed 's/%//' 2>/dev/null | tr -d '\n'); " +
                                     "load=$(cat /proc/loadavg 2>/dev/null | awk '{print $1}' | tr -d '\n'); " +
                                     "up=$(uptime -p 2>/dev/null | sed 's/up //'); " +
                                     "echo \"${cpu:-0}|${ram:-0}|${ssd:-0}|${load:-0.0}|${up:-N/A}\"";

                    string sysCmd = await localSsh.ExecuteCommandAsync(bashCmd);
                    var parts = sysCmd.Trim().Split('|');

                    if (parts.Length >= 5)
                    {
                        if (double.TryParse(parts[0].Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double c)) CpuUsage = (int)c;
                        if (int.TryParse(parts[1], out int r)) RamUsage = r;
                        if (int.TryParse(parts[2], out int s)) SsdUsage = s;
                        if (double.TryParse(parts[3].Replace(',', '.'), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double l)) LoadAverage = l.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

                        string rawUp = parts[4];
                        rawUp = rawUp.Replace(" weeks", "w").Replace(" week", "w")
                                     .Replace(" days", "d").Replace(" day", "d")
                                     .Replace(" hours", "h").Replace(" hour", "h")
                                     .Replace(" minutes", "m").Replace(" minute", "m")
                                     .Replace(",", "");

                        var upParts = rawUp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (upParts.Length > 2)
                        {
                            Uptime = $"{upParts[0]} {upParts[1]}\n{string.Join(" ", upParts.Skip(2))}";
                        }
                        else
                        {
                            Uptime = rawUp;
                        }
                    }
                    else
                    {
                        CpuUsage = res.Cpu; RamUsage = res.Ram; SsdUsage = res.Ssd; Uptime = res.Uptime; LoadAverage = res.LoadAvg;
                    }
                }
                catch
                {
                    CpuUsage = res.Cpu; RamUsage = res.Ram; SsdUsage = res.Ssd; Uptime = res.Uptime; LoadAverage = res.LoadAvg;
                }

                try
                {
                    string tcpCmd = await localSsh.ExecuteCommandAsync("ss -s | awk '/^TCP:/ {print $2}'");
                    if (int.TryParse(tcpCmd.Trim(), out int tcpCount)) TcpConnections = tcpCount;
                    else TcpConnections = res.TcpConnections;
                }
                catch { TcpConnections = res.TcpConnections; }

                string coreStatusCmd = $"systemctl is-active {serviceName}";
                string coreStatusStr = (await localSsh.ExecuteCommandAsync(coreStatusCmd)).Trim();
                coreStatusStr = coreStatusStr == "active" ? "Active" : "Stopped";

                string journalLogsCmd = $"journalctl -u {serviceName} -n 5 --no-pager";
                string journalLogs = await localSsh.ExecuteCommandAsync(journalLogsCmd);

                string accessLogs = await localSsh.ExecuteCommandAsync(isSingBox
                    ? "journalctl -u sing-box -n 5 --no-pager | grep INFO || echo 'Нет логов'"
                    : "tail -n 5 /var/log/xray/access.log 2>/dev/null || echo 'Нет логов'");

                string grepTest = await localSsh.ExecuteCommandAsync(isSingBox
                    ? "journalctl -u sing-box -n 50 --no-pager | grep -iE 'inbound connection|sniff' | tail -n 3"
                    : "tail -n 50 /var/log/xray/access.log 2>/dev/null | grep -E 'accepted|rejected' | tail -n 3");

                var coreStats = await _monitorService.GetCoreStatusInfoAsync(localSsh);
                var allOnlineStats = await _monitorService.GetUserOnlineStatsAsync(localSsh);

                string recentLogsCmd = isSingBox
                    ? "journalctl -u sing-box --since \"1 min ago\" --no-pager | grep 'inbound connection'"
                    : "tail -n 200 /var/log/xray/access.log 2>/dev/null | grep 'accepted'";

                string recentLogs = await localSsh.ExecuteCommandAsync(recentLogsCmd);
                var activeUsernames = new HashSet<string>();

                if (!string.IsNullOrWhiteSpace(recentLogs))
                {
                    var lines = recentLogs.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        if (isSingBox)
                        {
                            int inboundIdx = line.IndexOf("inbound connection");
                            if (inboundIdx != -1)
                            {
                                string prefix = line.Substring(0, inboundIdx);
                                int lastBracketClose = prefix.LastIndexOf(']');
                                if (lastBracketClose != -1)
                                {
                                    int lastBracketOpen = prefix.LastIndexOf('[', lastBracketClose);
                                    if (lastBracketOpen != -1)
                                    {
                                        string potentialUser = prefix.Substring(lastBracketOpen + 1, lastBracketClose - lastBracketOpen - 1).Trim();
                                        if (Clients.Any(c => c.Email == potentialUser)) activeUsernames.Add(potentialUser);
                                    }
                                }
                            }
                        }
                        else
                        {
                            var logParts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                            foreach (var part in logParts)
                            {
                                if (part.StartsWith("email:")) activeUsernames.Add(part.Replace("email:", "").Trim());
                                else if (part.StartsWith("[") && part.EndsWith("]"))
                                {
                                    string potentialUser = part.Trim('[', ']');
                                    if (Clients.Any(c => c.Email == potentialUser)) activeUsernames.Add(potentialUser);
                                }
                            }
                        }
                    }
                }

                string rulesDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules");
                string rulesFile = Path.Combine(rulesDir, "torrent_domains.txt");
                List<string> torrentDomains = new List<string> { "torrent", "tracker", "rutracker", "nnmclub", "kinozal", "rutor", "piratebay" };

                if (File.Exists(rulesFile))
                {
                    var ruleLines = await File.ReadAllLinesAsync(rulesFile);
                    var validLines = ruleLines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim().ToLower()).ToList();
                    if (validLines.Any()) torrentDomains = validLines;
                }

                string rawViolationLogs = isSingBox
                    ? await localSsh.ExecuteCommandAsync("journalctl -u sing-box --since \"1 min ago\" --no-pager | grep -iE 'inbound connection|sniffed'")
                    : await localSsh.ExecuteCommandAsync("tail -n 200 /var/log/xray/access.log 2>/dev/null | grep -E 'accepted|rejected|torrent-logger'");

                var violationsBatch = new List<(string Email, string ViolationType)>();
                if (!string.IsNullOrWhiteSpace(rawViolationLogs))
                {
                    var vlines = rawViolationLogs.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    var connDict = new Dictionary<string, (string User, string Domain)>();

                    foreach (var line in vlines)
                    {
                        try
                        {
                            if (isSingBox)
                            {
                                string id = null;
                                int idStart = line.IndexOf(" [");
                                if (idStart != -1)
                                {
                                    int idEnd = line.IndexOf(' ', idStart + 2);
                                    if (idEnd != -1) id = line.Substring(idStart + 2, idEnd - idStart - 2);
                                }
                                if (string.IsNullOrEmpty(id)) continue;

                                if (!connDict.ContainsKey(id)) connDict[id] = ("Unknown", "Unknown");
                                var info = connDict[id];

                                if (line.Contains("inbound connection"))
                                {
                                    int uStart = line.IndexOf("]: [");
                                    if (uStart != -1)
                                    {
                                        int uEnd = line.IndexOf("] inbound", uStart);
                                        if (uEnd != -1) info.User = line.Substring(uStart + 4, uEnd - uStart - 4);
                                    }
                                    int toIdx = line.IndexOf("to ");
                                    if (toIdx != -1)
                                    {
                                        string destIp = line.Substring(toIdx + 3).Replace("tcp:", "").Replace("udp:", "").Split(':')[0];
                                        if (info.Domain == "Unknown") info.Domain = destIp;
                                    }
                                }
                                else if (line.Contains("sniffed") && line.Contains("domain: "))
                                {
                                    int dStart = line.IndexOf("domain: ");
                                    if (dStart != -1)
                                    {
                                        string dom = line.Substring(dStart + 8).Split(',')[0].Trim();
                                        if (!string.IsNullOrEmpty(dom)) info.Domain = dom;
                                    }
                                }
                                connDict[id] = info;
                            }
                            else
                            {
                                string domain = "Unknown";
                                string violatorEmail = "";
                                var logParts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                for (int i = 0; i < logParts.Length; i++)
                                {
                                    if (logParts[i] == "accepted" || logParts[i] == "rejected" || logParts[i] == "[torrent-logger]")
                                    {
                                        if (i + 1 < logParts.Length) domain = logParts[i + 1].Replace("tcp:", "").Replace("udp:", "").Split(':')[0];
                                    }
                                    if (logParts[i].StartsWith("email:")) violatorEmail = logParts[i].Replace("email:", "").Trim();
                                    else if (logParts[i].StartsWith("[") && logParts[i].EndsWith("]"))
                                    {
                                        string potentialUser = logParts[i].Trim('[', ']');
                                        if (Clients.Any(c => c.Email == potentialUser)) violatorEmail = potentialUser;
                                    }
                                }

                                if (!string.IsNullOrEmpty(violatorEmail) && domain != "Unknown")
                                {
                                    string domainLower = domain.ToLower();
                                    if (torrentDomains.Any(td => domainLower.Contains(td)))
                                    {
                                        violationsBatch.Add((violatorEmail, $"Трекер / P2P: {domain}"));
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    if (isSingBox)
                    {
                        foreach (var kvp in connDict)
                        {
                            if (kvp.Value.User != "Unknown" && kvp.Value.Domain != "Unknown")
                            {
                                string dLower = kvp.Value.Domain.ToLower();
                                if (torrentDomains.Any(td => dLower.Contains(td)))
                                {
                                    violationsBatch.Add((kvp.Value.User, $"Трекер / P2P: {kvp.Value.Domain}"));
                                }
                            }
                        }
                    }
                }
                violationsBatch = violationsBatch.Distinct().ToList();

                var trafficStats = new Dictionary<string, long>();
                if (!isSingBox)
                {
                    trafficStats = await _userManager.GetTrafficStatsAsync(localSsh);
                }
                else
                {
                    string netBytesCmd = "IFACE=$(ip route | grep default | awk '{print $5}' | head -n1); echo $(( $(cat /sys/class/net/$IFACE/statistics/rx_bytes 2>/dev/null || echo 0) + $(cat /sys/class/net/$IFACE/statistics/tx_bytes 2>/dev/null || echo 0) ))";
                    string netBytesStr = await localSsh.ExecuteCommandAsync(netBytesCmd);

                    if (long.TryParse(netBytesStr.Trim(), out long currentTotalServerBytes))
                    {
                        long serverTrafficDelta = 0;
                        if (_previousTotalServerBytes > 0 && currentTotalServerBytes > _previousTotalServerBytes)
                            serverTrafficDelta = currentTotalServerBytes - _previousTotalServerBytes;
                        _previousTotalServerBytes = currentTotalServerBytes;

                        int activeUiUsersCount = activeUsernames.Count > 0 ? activeUsernames.Count : 1;
                        long bytesPerUser = serverTrafficDelta / activeUiUsersCount;

                        foreach (var uname in activeUsernames)
                        {
                            long prevUserBytes = _previousTrafficStats.TryGetValue(uname, out long p) ? p : 0;
                            trafficStats[uname] = prevUserBytes + bytesPerUser;
                        }
                    }
                    await _singBoxUserManager.GetTrafficStatsAsync(localSsh);
                }

                // === ИСПРАВЛЕНИЕ: ДОБАВЛЕНО ОБЪЯВЛЕНИЕ trafficBatch ===
                var trafficBatch = new Dictionary<string, long>();
                var connectionBatch = new List<(string Email, string Ip, string Country)>();

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    CoreTitleLabel = $"Ядро ({displayCoreName})";

                    XrayStatus = coreStatusStr; XrayVersion = coreStats.Version; XrayConfigStatus = coreStats.ConfigStatus;
                    XrayLastError = coreStats.LastError;
                    XrayLogs = $"=== СИСТЕМНЫЙ ЖУРНАЛ ===\n{journalLogs.Trim()}\n\n=== ACCESS.LOG ===\n{accessLogs.Trim()}\n\n=== ТЕСТ ПАРСЕРА ===\n{grepTest.Trim()}";

                    long currentTotalBytes = 0; bool dbNeedsUpdate = false;
                    if (DateTime.Today != _currentDay) { _dailyIps.Clear(); _currentDay = DateTime.Today; }

                    foreach (var client in Clients)
                    {
                        string email = client.Email ?? "Unknown";
                        long delta = 0;
                        if (trafficStats.TryGetValue(email, out long currentXrayBytes))
                        {
                            long prev = _previousTrafficStats.TryGetValue(email, out long p) ? p : 0;
                            delta = currentXrayBytes >= prev ? currentXrayBytes - prev : currentXrayBytes;
                            if (delta > 0) { client.TrafficUsed += delta; dbNeedsUpdate = true; trafficBatch[email] = delta; }
                            _previousTrafficStats[email] = currentXrayBytes;
                        }

                        if (activeUsernames.Contains(email))
                        {
                            var userLog = allOnlineStats.FirstOrDefault(s => s.Email == email);
                            client.ActiveConnections = userLog != null && userLog.ActiveSessions > 0 ? userLog.ActiveSessions : 1;
                            client.LastOnline = DateTime.Now;

                            if (userLog != null)
                            {
                                client.LastIp = userLog.LastIp;
                                if (!string.IsNullOrEmpty(userLog.Country)) client.Country = userLog.Country;
                                connectionBatch.Add((email, userLog.LastIp ?? "", client.Country ?? ""));
                            }
                        }
                        else
                        {
                            if (client.LastOnline.HasValue && (DateTime.Now - client.LastOnline.Value).TotalMinutes <= 1)
                                client.ActiveConnections = 1;
                            else
                                client.ActiveConnections = 0;
                        }

                        if (client.IsAntiFraudEnabled && client.ActiveConnections > 0)
                        {
                            string antiFraudReason = "";
                            string currentIp = client.LastIp ?? "";

                            if (!string.IsNullOrEmpty(currentIp))
                            {
                                if (!_dailyIps.ContainsKey(email)) _dailyIps[email] = new HashSet<string>();
                                _dailyIps[email].Add(currentIp);
                            }

                            bool geoJump = false;
                            string curCode = (client.Country?.Length >= 2) ? client.Country.Substring(client.Country.Length - 2) : "";
                            if (!string.IsNullOrEmpty(curCode) && curCode != "??")
                            {
                                if (_lastKnownCountry.TryGetValue(email, out string lastC) && lastC != curCode &&
                                    _lastKnownCountryTime.TryGetValue(email, out DateTime lastT) && (DateTime.Now - lastT).TotalHours < 2) geoJump = true;
                                if (!geoJump) { _lastKnownCountry[email] = curCode; _lastKnownCountryTime[email] = DateTime.Now; }
                            }

                            if (client.ActiveConnections > 2) antiFraudReason = "ФРОД: >2 Устройств";
                            else if (_dailyIps.ContainsKey(email) && _dailyIps[email].Count > 5) antiFraudReason = "ФРОД: >5 IP за сутки";
                            else if (geoJump) antiFraudReason = "ФРОД: Резкая смена страны";
                            else if (delta > 1073741824L) antiFraudReason = "ФРОД: Скачок трафика";

                            if (!string.IsNullOrEmpty(antiFraudReason) && client.IsActive)
                            {
                                client.IsActive = false; client.Note = antiFraudReason; dbNeedsUpdate = true; _ = BlockUserAsync(client, antiFraudReason);
                            }
                        }

                        currentTotalBytes += client.TrafficUsed;
                        bool isExceeded = client.TrafficLimit > 0 && client.TrafficUsed >= client.TrafficLimit;
                        bool isExpired = client.ExpiryDate.HasValue && client.ExpiryDate.Value.Date <= DateTime.Now.Date;
                        if ((isExceeded || isExpired) && client.IsActive) { client.IsActive = false; _ = BlockUserAsync(client, isExceeded ? "Превышен лимит" : "Истек срок"); }
                    }

                    if (dbNeedsUpdate && SelectedServer != null)
                    {
                        if (isSingBox) _ = _singBoxUserManager.SaveTrafficToDbAsync(ip, Clients);
                        else _ = _userManager.SaveTrafficToDbAsync(ip, Clients);
                    }

                    TotalUsers = Clients.Count; ActiveUsers = Clients.Count(c => c.ActiveConnections > 0); TotalTraffic = FormatBytes(currentTotalBytes);
                });

                _ = _analyticsService.SaveBatchAsync(ip, trafficBatch, connectionBatch, violationsBatch);
                await Task.Delay(5000, token);
            }
        }
        catch (TaskCanceledException) { }
        catch { ServerStatus = "Связь потеряна"; }
        finally { localSsh.Disconnect(); if (_currentMonitoringSsh == localSsh) { _currentMonitoringSsh = null; IsMonitoringActive = false; } }
    }

    private async Task LoadUsersAsync()
    {
        var ssh = _currentMonitoringSsh;
        var server = SelectedServer;
        if (ssh == null || !ssh.IsConnected || server == null) return;

        string ip = server.IpAddress ?? "";

        // === ИСПРАВЛЕНИЕ: Жестко берем тип ядра из базы данных! ===
        bool isSingBox = server.CoreType == "sing-box";

        _logger.Log("MONITORING", $"[LoadUsers] Запрос юзеров. Ядро по БД: {(isSingBox ? "Sing-Box" : "Xray")}");

        var realUsers = isSingBox
            ? await _singBoxUserManager.GetUsersAsync(ssh, ip)
            : await _userManager.GetUsersAsync(ssh, ip);

        _logger.Log("MONITORING", $"[LoadUsers] Из ядра получено {realUsers.Count} юзеров. UI обновлен.");

        System.Windows.Application.Current.Dispatcher.Invoke(() => { Clients.Clear(); foreach (var u in realUsers) Clients.Add(u); });
    }

    private async Task BlockUserAsync(VpnClient client, string reason)
    {
        var ssh = _currentMonitoringSsh;
        var server = SelectedServer;
        if (ssh == null || !ssh.IsConnected || server == null) return;

        string email = client.Email ?? "Unknown";
        string ip = server.IpAddress ?? "";

        // === ИСПРАВЛЕНИЕ: Жестко берем тип ядра из базы данных! ===
        bool isSingBox = server.CoreType == "sing-box";

        ServerStatus = $"Блокировка {email} ({reason})...";
        _logger.Log("MONITORING", $"[BlockUser] Блокировка {email}. Ядро по БД: {(isSingBox ? "Sing-Box" : "Xray")}");

        var (success, msg) = isSingBox
            ? await _singBoxUserManager.ToggleUserStatusAsync(ssh, ip, email, false)
            : await _userManager.ToggleUserStatusAsync(ssh, ip, email, false);

        ServerStatus = success ? $"Онлайн ({email} заблокирован)" : $"Ошибка блокировки: {msg}";
    }
}