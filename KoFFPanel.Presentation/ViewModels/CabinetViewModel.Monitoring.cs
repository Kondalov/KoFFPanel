using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;

namespace KoFFPanel.Presentation.ViewModels;

public partial class CabinetViewModel
{
    [ObservableProperty]
    private string _coreTitleLabel = "Ядро (Ожидание)";

    // Переменная для хранения общего физического трафика сервера (Для эвристики Sing-box)
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
                CpuUsage = res.Cpu; RamUsage = res.Ram; SsdUsage = res.Ssd; Uptime = res.Uptime; LoadAverage = res.LoadAvg;
                NetworkSpeed = res.NetworkSpeed; XrayProcesses = res.XrayProcesses; TcpConnections = res.TcpConnections; SynRecv = res.SynRecv; ErrorRate = res.ErrorRate;

                string activeCoreCmd = "systemctl is-active --quiet sing-box && echo 'Sing-box' || echo 'Xray-core'";
                string activeCoreName = (await localSsh.ExecuteCommandAsync(activeCoreCmd)).Trim();
                bool isSingBox = activeCoreName == "Sing-box";

                string coreStatusCmd = $"systemctl is-active {activeCoreName.ToLower()}";
                string coreStatusStr = (await localSsh.ExecuteCommandAsync(coreStatusCmd)).Trim();
                coreStatusStr = coreStatusStr == "active" ? "Active" : "Stopped";

                string journalLogsCmd = $"journalctl -u {activeCoreName.ToLower()} -n 5 --no-pager";
                string journalLogs = await localSsh.ExecuteCommandAsync(journalLogsCmd);

                string accessLogs = await localSsh.ExecuteCommandAsync("if [ \"$(systemctl is-active sing-box)\" = \"active\" ]; then journalctl -u sing-box -n 5 --no-pager | grep INFO || echo 'Нет логов'; else tail -n 5 /var/log/xray/access.log 2>/dev/null || echo 'Нет логов'; fi");
                string grepTest = await localSsh.ExecuteCommandAsync("if [ \"$(systemctl is-active sing-box)\" = \"active\" ]; then journalctl -u sing-box -n 50 --no-pager | grep -E 'inbound connection' | tail -n 3; else tail -n 50 /var/log/xray/access.log 2>/dev/null | grep -E 'accepted|rejected' | tail -n 3; fi");

                var coreStats = await _monitorService.GetCoreStatusInfoAsync(localSsh);
                var onlineStats = await _monitorService.GetUserOnlineStatsAsync(localSsh);

                _logger.Log("DIAGNOSTIC-RAW", $"Парсер логов нашел {onlineStats.Count} сессий.");

                // === 1. БРОНЕБОЙНЫЙ C#-ПАРСЕР НАРУШЕНИЙ (DMCA) ===
                string rawViolationLogs = "";
                if (isSingBox)
                {
                    rawViolationLogs = await localSsh.ExecuteCommandAsync("journalctl -u sing-box -n 2000 --no-pager | grep -E 'inbound connection to|outbound/block'");
                }
                else
                {
                    rawViolationLogs = await localSsh.ExecuteCommandAsync("tail -n 1000 /var/log/xray/access.log 2>/dev/null | grep -E 'torrent-logger|rejected'");
                }

                var violationsBatch = new List<(string Email, string ViolationType)>();
                if (!string.IsNullOrWhiteSpace(rawViolationLogs))
                {
                    var vlines = rawViolationLogs.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
                    if (isSingBox)
                    {
                        var idToUser = new Dictionary<string, string>();
                        foreach (var line in vlines)
                        {
                            try
                            {
                                if (!line.Contains("INFO [")) continue;
                                int idStart = line.IndexOf("INFO [") + 6;
                                int idEnd = line.IndexOf(' ', idStart);
                                if (idStart < 6 || idEnd <= idStart) continue;
                                string id = line.Substring(idStart, idEnd - idStart);

                                if (line.Contains("inbound connection to"))
                                {
                                    int userStart = line.IndexOf("]: [");
                                    if (userStart != -1)
                                    {
                                        userStart += 4;
                                        int userEnd = line.IndexOf(']', userStart);
                                        if (userEnd > userStart) idToUser[id] = line.Substring(userStart, userEnd - userStart).Trim();
                                    }
                                }
                                else if (line.Contains("outbound/block"))
                                {
                                    if (idToUser.TryGetValue(id, out string violatorEmail))
                                    {
                                        string domain = "Unknown";
                                        int destStart = line.IndexOf("connection to ");
                                        if (destStart != -1) domain = line.Substring(destStart + 14).Replace("tcp:", "").Replace("udp:", "").Split(':')[0].Trim();

                                        violationsBatch.Add((violatorEmail, $"Трекер / P2P: {domain}"));
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        foreach (var line in vlines)
                        {
                            try
                            {
                                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                                string dest = "Unknown"; string violatorEmail = "";
                                for (int i = 0; i < parts.Length; i++)
                                {
                                    if (parts[i] == "[torrent-logger]" || parts[i] == "rejected")
                                    {
                                        if (i + 1 < parts.Length) dest = parts[i + 1].Replace("tcp:", "").Replace("udp:", "").Split(':')[0];
                                    }
                                    if (parts[i] == "email:" && i + 1 < parts.Length) violatorEmail = parts[i + 1].Replace("]", "").Trim();
                                }
                                if (!string.IsNullOrEmpty(violatorEmail))
                                {
                                    violationsBatch.Add((violatorEmail, $"Трекер / P2P: {dest}"));
                                }
                            }
                            catch { }
                        }
                    }
                }
                violationsBatch = violationsBatch.Distinct().ToList();

                // === 2. ЭВРИСТИКА ТРАФИКА ===
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

                        // Делим трафик ТОЛЬКО между активными юзерами в интерфейсе
                        int activeUiUsersCount = Clients.Count(c => c.ActiveConnections > 0);
                        if (activeUiUsersCount == 0) activeUiUsersCount = 1; // Защита от деления на ноль

                        long bytesPerUser = serverTrafficDelta / activeUiUsersCount;

                        foreach (var c in Clients.Where(c => c.ActiveConnections > 0))
                        {
                            long prevUserBytes = _previousTrafficStats.TryGetValue(c.Email ?? "", out long p) ? p : 0;
                            trafficStats[c.Email ?? ""] = prevUserBytes + bytesPerUser;
                        }
                    }
                    await _singBoxUserManager.GetTrafficStatsAsync(localSsh); // Заглушка
                }

                var trafficBatch = new Dictionary<string, long>();
                var connectionBatch = new List<(string Email, string Ip, string Country)>();

                // === 3. ОБНОВЛЕНИЕ UI И АЛГОРИТМ "ИММУНИТЕТА" ===
                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    CoreTitleLabel = $"Ядро ({activeCoreName})";

                    XrayStatus = coreStatusStr; XrayVersion = coreStats.Version; XrayConfigStatus = coreStats.ConfigStatus;
                    XrayUptime = coreStats.Uptime; XrayMemory = coreStats.MemoryUsage; XrayLastError = coreStats.LastError;
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

                        // АЛГОРИТМ "SOFT LEASH" (3 МИНУТЫ ИММУНИТЕТА)
                        var userLog = onlineStats.FirstOrDefault(s => s.Email == email);
                        if (userLog != null)
                        {
                            // Юзер есть в свежих логах -> Обновляем время
                            client.ActiveConnections = userLog.ActiveSessions > 0 ? userLog.ActiveSessions : 1;
                            client.LastOnline = DateTime.Now;
                            client.LastIp = userLog.LastIp;
                            if (!string.IsNullOrEmpty(userLog.Country)) client.Country = userLog.Country;
                            connectionBatch.Add((email, userLog.LastIp ?? "", client.Country ?? ""));
                        }
                        else
                        {
                            // Юзера нет в логах. Проверяем иммунитет (3 минуты)
                            // ИСПРАВЛЕНИЕ: Безопасная работа с Nullable DateTime
                            if (client.LastOnline.HasValue && (DateTime.Now - client.LastOnline.Value).TotalMinutes < 3)
                            {
                                client.ActiveConnections = 1; // Иммунитет действует, оставляем онлайн
                            }
                            else
                            {
                                client.ActiveConnections = 0; // Время вышло, жесткий оффлайн
                            }
                        }

                        // Логика антифрода
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

                    if (dbNeedsUpdate && SelectedServer != null) _ = _userManager.SaveTrafficToDbAsync(ip, Clients);
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

        var realUsers = IsSingBoxActive()
            ? await _singBoxUserManager.GetUsersAsync(ssh, ip)
            : await _userManager.GetUsersAsync(ssh, ip);

        System.Windows.Application.Current.Dispatcher.Invoke(() => { Clients.Clear(); foreach (var u in realUsers) Clients.Add(u); });
    }

    private async Task BlockUserAsync(VpnClient client, string reason)
    {
        var ssh = _currentMonitoringSsh;
        var server = SelectedServer;
        if (ssh == null || !ssh.IsConnected || server == null) return;

        string email = client.Email ?? "Unknown";
        string ip = server.IpAddress ?? "";

        ServerStatus = $"Блокировка {email} ({reason})...";

        var (success, msg) = IsSingBoxActive()
            ? await _singBoxUserManager.ToggleUserStatusAsync(ssh, ip, email, false)
            : await _userManager.ToggleUserStatusAsync(ssh, ip, email, false);

        ServerStatus = success ? $"Онлайн ({email} заблокирован)" : $"Ошибка блокировки: {msg}";
    }
}