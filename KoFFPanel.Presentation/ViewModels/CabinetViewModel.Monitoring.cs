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

                string coreStatusCmd = $"systemctl is-active {activeCoreName.ToLower()}";
                string coreStatusStr = (await localSsh.ExecuteCommandAsync(coreStatusCmd)).Trim();
                coreStatusStr = coreStatusStr == "active" ? "Active" : "Stopped";

                string journalLogsCmd = $"journalctl -u {activeCoreName.ToLower()} -n 5 --no-pager";
                string journalLogs = await localSsh.ExecuteCommandAsync(journalLogsCmd);

                string accessLogs = await localSsh.ExecuteCommandAsync("if [ \"$(systemctl is-active sing-box)\" = \"active\" ]; then journalctl -u sing-box -n 5 --no-pager | grep INFO || echo 'Нет логов'; else tail -n 5 /var/log/xray/access.log 2>/dev/null || echo 'Нет логов'; fi");
                string grepTest = await localSsh.ExecuteCommandAsync("if [ \"$(systemctl is-active sing-box)\" = \"active\" ]; then journalctl -u sing-box -n 50 --no-pager | grep -E 'inbound connection' | tail -n 3; else tail -n 50 /var/log/xray/access.log 2>/dev/null | grep -E 'accepted|rejected' | tail -n 3; fi");
                string violationTest = await localSsh.ExecuteCommandAsync("if [ \"$(systemctl is-active sing-box)\" = \"active\" ]; then echo \"\"; else tail -n 1000 /var/log/xray/access.log 2>/dev/null | awk '/\\[torrent-logger\\]/ && /email:/ { dest=$6; gsub(/^tcp:|^udp:|:[0-9]+$/, \"\", dest); email=$NF; print email \"|\" dest }' | sort | uniq; fi");

                var coreStats = await _monitorService.GetCoreStatusInfoAsync(localSsh);

                // === ПУЛЕНЕПРОБИВАЕМОЕ ПОЛУЧЕНИЕ ОНЛАЙНА ===
                var onlineStats = await _monitorService.GetUserOnlineStatsAsync(localSsh);

                _logger.Log("DIAGNOSTIC-RAW", $"C# Парсер успешно завершил работу. Найдено сессий: {onlineStats.Count}");
                foreach (var stat in onlineStats)
                {
                    _logger.Log("DIAGNOSTIC-USER", $"User: {stat.Email} | IP: {stat.LastIp} | Sess: {stat.ActiveSessions} | {stat.Country}");
                }

                var trafficStats = activeCoreName == "Xray-core"
                    ? await _userManager.GetTrafficStatsAsync(localSsh)
                    : await _singBoxUserManager.GetTrafficStatsAsync(localSsh);

                var trafficBatch = new Dictionary<string, long>();
                var connectionBatch = new List<(string Email, string Ip, string Country)>();
                var violationsBatch = new List<(string Email, string ViolationType)>();

                if (!string.IsNullOrWhiteSpace(violationTest))
                {
                    var lines = violationTest.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length == 2) violationsBatch.Add((parts[0].Trim(), $"Трекер / P2P: {parts[1].Trim()}"));
                    }
                }

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

                        var userLog = onlineStats.FirstOrDefault(s => s.Email == email);
                        if (userLog != null)
                        {
                            client.LastIp = userLog.LastIp; client.ActiveConnections = userLog.ActiveSessions; client.LastOnline = DateTime.Now;
                            if (!string.IsNullOrEmpty(userLog.Country)) client.Country = userLog.Country;

                            connectionBatch.Add((email, userLog.LastIp ?? "", client.Country ?? ""));

                            if (client.IsAntiFraudEnabled)
                            {
                                string antiFraudReason = "";
                                if (!_dailyIps.ContainsKey(email)) _dailyIps[email] = new HashSet<string>();
                                _dailyIps[email].Add(userLog.LastIp ?? "");

                                bool geoJump = false;
                                string curCode = (userLog.Country?.Length >= 2) ? userLog.Country.Substring(userLog.Country.Length - 2) : "";
                                if (!string.IsNullOrEmpty(curCode) && curCode != "??")
                                {
                                    if (_lastKnownCountry.TryGetValue(email, out string lastC) && lastC != curCode &&
                                        _lastKnownCountryTime.TryGetValue(email, out DateTime lastT) && (DateTime.Now - lastT).TotalHours < 2) geoJump = true;
                                    if (!geoJump) { _lastKnownCountry[email] = curCode; _lastKnownCountryTime[email] = DateTime.Now; }
                                }

                                if (client.ActiveConnections > 2) antiFraudReason = "ФРОД: >2 Устройств";
                                else if (_dailyIps[email].Count > 5) antiFraudReason = "ФРОД: >5 IP за сутки";
                                else if (geoJump) antiFraudReason = "ФРОД: Резкая смена страны";
                                else if (delta > 1073741824L) antiFraudReason = "ФРОД: Скачок трафика";

                                if (!string.IsNullOrEmpty(antiFraudReason) && client.IsActive)
                                {
                                    client.IsActive = false; client.Note = antiFraudReason; dbNeedsUpdate = true; _ = BlockUserAsync(client, antiFraudReason);
                                }
                            }
                        }
                        else client.ActiveConnections = 0;

                        currentTotalBytes += client.TrafficUsed;
                        bool isExceeded = client.TrafficLimit > 0 && client.TrafficUsed >= client.TrafficLimit;
                        bool isExpired = client.ExpiryDate.HasValue && client.ExpiryDate.Value.Date <= DateTime.Now.Date;
                        if ((isExceeded || isExpired) && client.IsActive) { client.IsActive = false; _ = BlockUserAsync(client, isExceeded ? "Превышен лимит" : "Истек срок"); }
                    }

                    if (dbNeedsUpdate && SelectedServer != null) _ = _userManager.SaveTrafficToDbAsync(ip, Clients);
                    TotalUsers = Clients.Count; ActiveUsers = Clients.Count(c => c.IsActive); TotalTraffic = FormatBytes(currentTotalBytes);
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