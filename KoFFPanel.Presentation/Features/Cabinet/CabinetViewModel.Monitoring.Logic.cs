using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Application.Interfaces;
using System.IO;

namespace KoFFPanel.Presentation.Features.Cabinet;

public partial class CabinetViewModel
{
    private async Task UpdateSystemMetricsAsync(ISshService localSsh, ServerResources res)
    {
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
                Uptime = FormatUptime(parts[4]);
            }
            else { SetFallbackMetrics(res); }
        }
        catch { SetFallbackMetrics(res); }
    }

    private string FormatUptime(string rawUp)
    {
        rawUp = rawUp.Replace(" weeks", "w").Replace(" week", "w").Replace(" days", "d").Replace(" day", "d").Replace(" hours", "h").Replace(" hour", "h").Replace(" minutes", "m").Replace(" minute", "m").Replace(",", "");
        var upParts = rawUp.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return upParts.Length > 2 ? $"{upParts[0]} {upParts[1]}\n{string.Join(" ", upParts.Skip(2))}" : rawUp;
    }

    private void SetFallbackMetrics(ServerResources res)
    {
        CpuUsage = res.Cpu; RamUsage = res.Ram; SsdUsage = res.Ssd; Uptime = res.Uptime; LoadAverage = res.LoadAvg;
    }

    private async Task<int> GetTcpConnectionsCountAsync(ISshService localSsh, int fallback)
    {
        try
        {
            string tcpCmd = await localSsh.ExecuteCommandAsync("ss -s | awk '/^TCP:/ {print $2}'");
            return int.TryParse(tcpCmd.Trim(), out int tcpCount) ? tcpCount : fallback;
        }
        catch { return fallback; }
    }

    private async Task<string> GetAccessLogsAsync(ISshService localSsh, bool isSingBox, bool isTrustTunnel)
    {
        return await localSsh.ExecuteCommandAsync(
            isSingBox ? "journalctl -u sing-box -n 5 --no-pager | grep INFO || echo 'Нет логов'" :
            isTrustTunnel ? "journalctl -u trusttunnel -n 5 --no-pager || echo 'Нет логов'" :
            "tail -n 5 /var/log/xray/access.log 2>/dev/null || echo 'Нет логов'"
        );
    }

    private async Task<string> GetParserTestLogsAsync(ISshService localSsh, bool isSingBox, bool isTrustTunnel)
    {
        return await localSsh.ExecuteCommandAsync(
            isSingBox ? "journalctl -u sing-box -n 50 --no-pager | grep -iE 'inbound connection|sniff' | tail -n 3" :
            isTrustTunnel ? "journalctl -u trusttunnel -n 50 --no-pager | tail -n 3" :
            "tail -n 50 /var/log/xray/access.log 2>/dev/null | grep -E 'accepted|rejected' | tail -n 3"
        );
    }

    private async Task<HashSet<string>> GetActiveUsernamesAsync(ISshService localSsh, bool isSingBox, bool isTrustTunnel)
    {
        string cmd = isSingBox ? "journalctl -u sing-box --since \"1 min ago\" --no-pager | grep 'inbound connection'" :
                     isTrustTunnel ? "journalctl -u trusttunnel --since \"1 min ago\" --no-pager" :
                     "tail -n 200 /var/log/xray/access.log 2>/dev/null | grep 'accepted'";

        string recentLogs = await localSsh.ExecuteCommandAsync(cmd);
        var activeUsernames = new HashSet<string>();

        if (!string.IsNullOrWhiteSpace(recentLogs))
        {
            var lines = recentLogs.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (isSingBox) ProcessSingBoxLine(line, activeUsernames);
                else ProcessXrayLine(line, activeUsernames);
            }
        }
        return activeUsernames;
    }

    private void ProcessSingBoxLine(string line, HashSet<string> activeUsernames)
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

    private void ProcessXrayLine(string line, HashSet<string> activeUsernames)
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

    private async Task<List<(string Email, string ViolationType)>> ProcessViolationsAsync(ISshService localSsh, bool isSingBox, bool isTrustTunnel, HashSet<string> activeUsernames)
    {
        List<string> torrentDomains = await LoadTorrentDomainsAsync();
        string rawViolationLogs = isSingBox ? await localSsh.ExecuteCommandAsync("journalctl -u sing-box --since \"1 min ago\" --no-pager | grep -iE 'inbound connection|sniffed'") :
                                 isTrustTunnel ? "" :
                                 await localSsh.ExecuteCommandAsync("tail -n 200 /var/log/xray/access.log 2>/dev/null | grep -E 'accepted|rejected|torrent-logger'");

        var violationsBatch = new List<(string Email, string ViolationType)>();
        if (string.IsNullOrWhiteSpace(rawViolationLogs)) return violationsBatch;

        var vlines = rawViolationLogs.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
        if (isSingBox) ProcessSingBoxViolations(vlines, torrentDomains, violationsBatch);
        else ProcessXrayViolations(vlines, torrentDomains, violationsBatch);

        return violationsBatch.Distinct().ToList();
    }

    private async Task<List<string>> LoadTorrentDomainsAsync()
    {
        string rulesFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "rules", "torrent_domains.txt");
        if (File.Exists(rulesFile))
        {
            var ruleLines = await File.ReadAllLinesAsync(rulesFile);
            return ruleLines.Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l.Trim().ToLower()).ToList();
        }
        return new List<string> { "torrent", "tracker", "rutracker", "nnmclub", "kinozal", "rutor", "piratebay" };
    }

    private void ProcessSingBoxViolations(string[] lines, List<string> torrentDomains, List<(string, string)> batch)
    {
        var connDict = new Dictionary<string, (string User, string Domain)>();
        foreach (var line in lines)
        {
            int idStart = line.IndexOf(" [");
            if (idStart == -1) continue;
            int idEnd = line.IndexOf(' ', idStart + 2);
            if (idEnd == -1) continue;
            string id = line.Substring(idStart + 2, idEnd - idStart - 2);
            if (!connDict.ContainsKey(id)) connDict[id] = ("Unknown", "Unknown");
            var info = connDict[id];

            if (line.Contains("inbound connection"))
            {
                int uStart = line.IndexOf("]: [");
                if (uStart != -1) { int uEnd = line.IndexOf("] inbound", uStart); if (uEnd != -1) info.User = line.Substring(uStart + 4, uEnd - uStart - 4); }
                int toIdx = line.IndexOf("to ");
                if (toIdx != -1) { string destIp = line.Substring(toIdx + 3).Replace("tcp:", "").Replace("udp:", "").Split(':')[0]; if (info.Domain == "Unknown") info.Domain = destIp; }
            }
            else if (line.Contains("sniffed") && line.Contains("domain: "))
            {
                int dStart = line.IndexOf("domain: ");
                if (dStart != -1) { string dom = line.Substring(dStart + 8).Split(',')[0].Trim(); if (!string.IsNullOrEmpty(dom)) info.Domain = dom; }
            }
            connDict[id] = info;
        }
        foreach (var kvp in connDict)
        {
            if (kvp.Value.User != "Unknown" && kvp.Value.Domain != "Unknown" && torrentDomains.Any(td => kvp.Value.Domain.ToLower().Contains(td)))
                batch.Add((kvp.Value.User, $"Трекер / P2P: {kvp.Value.Domain}"));
        }
    }

    private void ProcessXrayViolations(string[] lines, List<string> torrentDomains, List<(string, string)> batch)
    {
        foreach (var line in lines)
        {
            string domain = "Unknown"; string violatorEmail = "";
            var logParts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < logParts.Length; i++)
            {
                if ((logParts[i] == "accepted" || logParts[i] == "rejected" || logParts[i] == "[torrent-logger]") && i + 1 < logParts.Length)
                    domain = logParts[i + 1].Replace("tcp:", "").Replace("udp:", "").Split(':')[0];
                if (logParts[i].StartsWith("email:")) violatorEmail = logParts[i].Replace("email:", "").Trim();
                else if (logParts[i].StartsWith("[") && logParts[i].EndsWith("]")) { string pot = logParts[i].Trim('[', ']'); if (Clients.Any(c => c.Email == pot)) violatorEmail = pot; }
            }
            if (!string.IsNullOrEmpty(violatorEmail) && domain != "Unknown" && torrentDomains.Any(td => domain.ToLower().Contains(td)))
                batch.Add((violatorEmail, $"Трекер / P2P: {domain}"));
        }
    }

    private async Task<Dictionary<string, long>> CalculateTrafficStatsAsync(ISshService localSsh, bool isSingBox, bool isTrustTunnel, HashSet<string> activeUsernames)
    {
        if (!isSingBox && !isTrustTunnel) return await _userManager.GetTrafficStatsAsync(localSsh);

        var trafficStats = new Dictionary<string, long>();
        string netBytesCmd = "IFACE=$(ip route | grep default | awk '{print $5}' | head -n1); echo $(( $(cat /sys/class/net/$IFACE/statistics/rx_bytes 2>/dev/null || echo 0) + $(cat /sys/class/net/$IFACE/statistics/tx_bytes 2>/dev/null || echo 0) ))";
        
        if (long.TryParse((await localSsh.ExecuteCommandAsync(netBytesCmd)).Trim(), out long currentTotalServerBytes))
        {
            if (_previousTotalServerBytes > 0 && currentTotalServerBytes > _previousTotalServerBytes)
            {
                long delta = currentTotalServerBytes - _previousTotalServerBytes;
                // Исключаем фоновый трафик системы (например 10KB/сек) если юзеров нет
                if (activeUsernames.Count > 0 && delta > 10240) 
                {
                    long bytesPerUser = delta / activeUsernames.Count;
                    foreach (var uname in activeUsernames) 
                    {
                        trafficStats[uname] = (_previousTrafficStats.TryGetValue(uname, out long p) ? p : 0) + bytesPerUser;
                    }
                }
            }
            _previousTotalServerBytes = currentTotalServerBytes;
        }

        return trafficStats;
    }

    private void UpdateUiAfterCycle(string displayCoreName, string coreStatusStr, CoreStatusInfo coreStats, string journalLogs, string accessLogs, string grepTest)
    {
        ActiveCoreTitle = $"Ядро ({displayCoreName})";
        XrayStatus = coreStatusStr; XrayVersion = coreStats.Version; XrayConfigStatus = coreStats.ConfigStatus;
        XrayLastError = coreStats.LastError; XrayUptime = coreStats.Uptime;
        XrayLogs = $"=== СИСТЕМНЫЙ ЖУРНАЛ ===\n{journalLogs.Trim()}\n\n=== ACCESS.LOG ===\n{accessLogs.Trim()}\n\n=== ТЕСТ ПАРСЕРА ===\n{grepTest.Trim()}";
    }

    private bool ProcessClientsAfterCycle(Dictionary<string, long> trafficStats, HashSet<string> activeUsernames, List<UserOnlineInfo> allOnlineStats, Dictionary<string, long> trafficBatch, List<(string, string, string)> connectionBatch)
    {
        bool dbNeedsUpdate = false;
        if (DateTime.Today != _currentDay) { _dailyIps.Clear(); _currentDay = DateTime.Today; }

        foreach (var client in Clients)
        {
            string email = client.Email ?? "Unknown";
            if (trafficStats.TryGetValue(email, out long currentBytes))
            {
                long prev = _previousTrafficStats.TryGetValue(email, out long p) ? p : 0;
                long delta = currentBytes >= prev ? currentBytes - prev : currentBytes;
                if (delta > 0) { client.TrafficUsed += delta; dbNeedsUpdate = true; trafficBatch[email] = delta; }
                _previousTrafficStats[email] = currentBytes;
            }

            if (activeUsernames.Contains(email))
            {
                var log = allOnlineStats.FirstOrDefault(s => s.Email == email);
                client.ActiveConnections = log != null && log.ActiveSessions > 0 ? log.ActiveSessions : 1;
                client.LastOnline = DateTime.Now;
                if (log != null) { client.LastIp = log.LastIp; if (!string.IsNullOrEmpty(log.Country)) client.Country = log.Country; connectionBatch.Add((email, log.LastIp ?? "", client.Country ?? "")); }
            }
            else client.ActiveConnections = (client.LastOnline.HasValue && (DateTime.Now - client.LastOnline.Value).TotalMinutes <= 1) ? 1 : 0;

            if (CheckAntiFraudAndLimits(client, trafficStats.TryGetValue(email, out long d) ? d : 0)) dbNeedsUpdate = true;
        }
        return dbNeedsUpdate;
    }

    private bool CheckAntiFraudAndLimits(VpnClient client, long delta)
    {
        if (!client.IsActive) return false;
        bool isExceeded = client.TrafficLimit > 0 && client.TrafficUsed >= client.TrafficLimit;
        bool isExpired = client.ExpiryDate.HasValue && client.ExpiryDate.Value.Date <= DateTime.Now.Date;
        if (isExceeded || isExpired) { client.IsActive = false; _ = BlockUserAsync(client, isExceeded ? "Превышен лимит" : "Истек срок"); return true; }

        if (client.IsAntiFraudEnabled && client.ActiveConnections > 0)
        {
            string reason = GetAntiFraudReason(client, delta);
            if (!string.IsNullOrEmpty(reason)) { client.IsActive = false; client.Note = reason; _ = BlockUserAsync(client, reason); return true; }
        }
        return false;
    }

    private string GetAntiFraudReason(VpnClient client, long delta)
    {
        string email = client.Email ?? ""; string currentIp = client.LastIp ?? "";
        if (!string.IsNullOrEmpty(currentIp)) { if (!_dailyIps.ContainsKey(email)) _dailyIps[email] = new HashSet<string>(); _dailyIps[email].Add(currentIp); }

        bool geoJump = false;
        string curCode = (client.Country?.Length >= 2) ? client.Country.Substring(client.Country.Length - 2) : "";
        if (!string.IsNullOrEmpty(curCode) && curCode != "??")
        {
            if (_lastKnownCountry.TryGetValue(email, out string lastC) && lastC != curCode && _lastKnownCountryTime.TryGetValue(email, out DateTime lastT) && (DateTime.Now - lastT).TotalHours < 2) geoJump = true;
            if (!geoJump) { _lastKnownCountry[email] = curCode; _lastKnownCountryTime[email] = DateTime.Now; }
        }

        if (client.ActiveConnections > 2) return "ФРОД: >2 Устройств";
        if (_dailyIps.ContainsKey(email) && _dailyIps[email].Count > 5) return "ФРОД: >5 IP за сутки";
        if (geoJump) return "ФРОД: Резкая смена страны";
        if (delta > 1073741824L) return "ФРОД: Скачок трафика";
        return "";
    }

    private async Task LoadUsersAsync()
    {
        var ssh = _currentMonitoringSsh; var server = SelectedServer;
        if (ssh == null || !ssh.IsConnected || server == null) return;
        string ip = server.IpAddress ?? "";
        var realUsers = server.CoreType == "sing-box" ? await _singBoxUserManager.GetUsersAsync(ssh, ip) :
                        (server.CoreType == "trusttunnel" ? await _trustTunnelUserManager.GetUsersAsync(ssh, ip) : await _userManager.GetUsersAsync(ssh, ip));
        System.Windows.Application.Current.Dispatcher.Invoke(() => { Clients.Clear(); foreach (var u in realUsers) Clients.Add(u); });
    }

    private async Task BlockUserAsync(VpnClient client, string reason)
    {
        var ssh = _currentMonitoringSsh; var server = SelectedServer;
        if (ssh == null || !ssh.IsConnected || server == null) return;
        string email = client.Email ?? "Unknown"; string ip = server.IpAddress ?? "";
        ServerStatus = $"Блокировка {email} ({reason})...";
        var (success, msg) = server.CoreType == "sing-box" ? await _singBoxUserManager.ToggleUserStatusAsync(ssh, ip, email, false) :
                            (server.CoreType == "trusttunnel" ? await _trustTunnelUserManager.ToggleUserStatusAsync(ssh, ip, email, false) : await _userManager.ToggleUserStatusAsync(ssh, ip, email, false));
        ServerStatus = success ? $"Онлайн ({email} заблокирован)" : $"Ошибка блокировки: {msg}";
    }
}
