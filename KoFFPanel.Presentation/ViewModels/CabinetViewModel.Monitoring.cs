using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Application.Interfaces;

namespace KoFFPanel.Presentation.ViewModels;

public partial class CabinetViewModel
{
    private async Task StartMonitoringLoopAsync(VpnProfile profile, CancellationToken token)
    {
        IsMonitoringActive = true; ServerStatus = "Подключение...";
        ISshService localSsh = _sshServiceFactory(); _currentMonitoringSsh = localSsh;

        if (await localSsh.ConnectAsync(profile.IpAddress, profile.Port, profile.Username, profile.Password, profile.KeyPath) != "SUCCESS")
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
                var pingResult = await _monitorService.PingServerAsync(profile.IpAddress); PingMs = pingResult.Success ? pingResult.RoundtripTime : 0;
                var res = await _monitorService.GetResourcesAsync(localSsh);
                CpuUsage = res.Cpu; RamUsage = res.Ram; SsdUsage = res.Ssd; Uptime = res.Uptime; LoadAverage = res.LoadAvg;
                NetworkSpeed = res.NetworkSpeed; XrayProcesses = res.XrayProcesses; TcpConnections = res.TcpConnections; SynRecv = res.SynRecv; ErrorRate = res.ErrorRate;

                var onlineStats = await _monitorService.GetUserOnlineStatsAsync(localSsh);
                var coreStats = await _monitorService.GetCoreStatusInfoAsync(localSsh);
                string xrayStatusStr = await _xrayService.GetCoreStatusAsync(localSsh);
                string journalLogs = await _xrayService.GetCoreLogsAsync(localSsh, 5);
                string accessLogs = await localSsh.ExecuteCommandAsync("tail -n 5 /var/log/xray/access.log 2>/dev/null");
                string grepTest = await localSsh.ExecuteCommandAsync("tail -n 50 /var/log/xray/access.log 2>/dev/null | grep -E 'accepted|rejected' | tail -n 3");

                // ИСПРАВЛЕНИЕ: Супер-парсер. Он берет $6 (целевой адрес), вырезает "tcp:" и порт ":443", и отдает связку "email|domain"
                string violationTest = await localSsh.ExecuteCommandAsync("tail -n 1000 /var/log/xray/access.log 2>/dev/null | awk '/\\[torrent-logger\\]/ && /email:/ { dest=$6; gsub(/^tcp:|^udp:|:[0-9]+$/, \"\", dest); email=$NF; print email \"|\" dest }' | sort | uniq");

                var trafficStats = await _userManager.GetTrafficStatsAsync(localSsh);

                var trafficBatch = new Dictionary<string, long>();
                var connectionBatch = new List<(string Email, string Ip, string Country)>();
                var violationsBatch = new List<(string Email, string ViolationType)>();

                // ИСПРАВЛЕНИЕ: Разбиваем строку на email и сам домен
                if (!string.IsNullOrWhiteSpace(violationTest))
                {
                    var lines = violationTest.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                    {
                        var parts = line.Split('|');
                        if (parts.Length == 2)
                        {
                            string email = parts[0].Trim();
                            string domain = parts[1].Trim();
                            violationsBatch.Add((email, $"Трекер / P2P: {domain}")); // Теперь в БД запишется конкретный сайт!
                        }
                    }
                }

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    XrayStatus = xrayStatusStr; XrayVersion = coreStats.Version; XrayConfigStatus = coreStats.ConfigStatus;
                    XrayUptime = coreStats.Uptime; XrayMemory = coreStats.MemoryUsage; XrayLastError = coreStats.LastError;
                    XrayLogs = $"=== СИСТЕМНЫЙ ЖУРНАЛ ===\n{journalLogs.Trim()}\n\n=== ACCESS.LOG ===\n{accessLogs.Trim()}\n\n=== ТЕСТ ПАРСЕРА ===\n{grepTest.Trim()}";

                    long currentTotalBytes = 0; bool dbNeedsUpdate = false;
                    if (DateTime.Today != _currentDay) { _dailyIps.Clear(); _currentDay = DateTime.Today; }

                    foreach (var client in Clients)
                    {
                        long delta = 0;
                        if (trafficStats.TryGetValue(client.Email, out long currentXrayBytes))
                        {
                            long prev = _previousTrafficStats.TryGetValue(client.Email, out long p) ? p : 0;
                            delta = currentXrayBytes >= prev ? currentXrayBytes - prev : currentXrayBytes;
                            if (delta > 0)
                            {
                                client.TrafficUsed += delta;
                                dbNeedsUpdate = true;
                                trafficBatch[client.Email] = delta;
                            }
                            _previousTrafficStats[client.Email] = currentXrayBytes;
                        }

                        var userLog = onlineStats.FirstOrDefault(s => s.Email == client.Email);
                        if (userLog != null)
                        {
                            client.LastIp = userLog.LastIp; client.ActiveConnections = userLog.ActiveSessions; client.LastOnline = DateTime.Now;
                            if (!string.IsNullOrEmpty(userLog.Country)) client.Country = userLog.Country;

                            connectionBatch.Add((client.Email, userLog.LastIp, client.Country));

                            if (client.IsAntiFraudEnabled)
                            {
                                string antiFraudReason = "";
                                if (!_dailyIps.ContainsKey(client.Email)) _dailyIps[client.Email] = new HashSet<string>();
                                _dailyIps[client.Email].Add(userLog.LastIp);

                                bool geoJump = false;
                                string curCode = userLog.Country.Length >= 2 ? userLog.Country.Substring(userLog.Country.Length - 2) : "";
                                if (!string.IsNullOrEmpty(curCode) && curCode != "??")
                                {
                                    if (_lastKnownCountry.TryGetValue(client.Email, out string lastC) && lastC != curCode &&
                                        _lastKnownCountryTime.TryGetValue(client.Email, out DateTime lastT) && (DateTime.Now - lastT).TotalHours < 2) geoJump = true;
                                    if (!geoJump) { _lastKnownCountry[client.Email] = curCode; _lastKnownCountryTime[client.Email] = DateTime.Now; }
                                }

                                if (client.ActiveConnections > 2) antiFraudReason = "ФРОД: >2 Устройств";
                                else if (_dailyIps[client.Email].Count > 5) antiFraudReason = "ФРОД: >5 IP за сутки";
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

                    if (dbNeedsUpdate && SelectedServer != null) _ = _userManager.SaveTrafficToDbAsync(SelectedServer.IpAddress, Clients);
                    TotalUsers = Clients.Count; ActiveUsers = Clients.Count(c => c.IsActive); TotalTraffic = FormatBytes(currentTotalBytes);
                });

                _ = _analyticsService.SaveBatchAsync(profile.IpAddress, trafficBatch, connectionBatch, violationsBatch);
                await Task.Delay(5000, token);
            }
        }
        catch (TaskCanceledException) { }
        catch { ServerStatus = "Связь потеряна"; }
        finally { localSsh.Disconnect(); if (_currentMonitoringSsh == localSsh) { _currentMonitoringSsh = null; IsMonitoringActive = false; } }
    }

    private async Task LoadUsersAsync()
    {
        if (_currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;
        var realUsers = await _userManager.GetUsersAsync(_currentMonitoringSsh, SelectedServer.IpAddress);
        System.Windows.Application.Current.Dispatcher.Invoke(() => { Clients.Clear(); foreach (var u in realUsers) Clients.Add(u); });
    }

    private async Task BlockUserAsync(VpnClient client, string reason)
    {
        if (_currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;
        ServerStatus = $"Блокировка {client.Email} ({reason})...";
        var (success, msg) = await _userManager.ToggleUserStatusAsync(_currentMonitoringSsh, SelectedServer.IpAddress, client.Email, false);
        ServerStatus = success ? $"Онлайн ({client.Email} заблокирован)" : $"Ошибка блокировки: {msg}";
    }
}