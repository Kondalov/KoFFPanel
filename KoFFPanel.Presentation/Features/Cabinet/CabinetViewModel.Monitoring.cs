using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Application.Interfaces;
using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

namespace KoFFPanel.Presentation.Features.Cabinet;

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
        bool isTrustTunnel = profile.CoreType == "trusttunnel";
        
        string displayCoreName = isSingBox ? "Sing-box" : (isTrustTunnel ? "TrustTunnel" : "Xray-core");
        if (profile.Inbounds.Any(i => i.Protocol.ToLower() == "trusttunnel") && !isTrustTunnel)
        {
            displayCoreName += " + TrustTunnel";
        }

        string serviceName = isSingBox ? "sing-box" : (isTrustTunnel ? "trusttunnel" : "xray");

        _logger.Log("MONITORING", $"[START] Запуск цикла мониторинга. Ядро: {displayCoreName.ToUpper()}");

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
                await RunMonitoringCycleStepAsync(localSsh, profile, isSingBox, isTrustTunnel, serviceName, ip, displayCoreName, token);
                await Task.Delay(5000, token);
            }
        }
        catch (TaskCanceledException) { }
        catch { ServerStatus = "Связь потеряна"; }
        finally { localSsh.Disconnect(); if (_currentMonitoringSsh == localSsh) { _currentMonitoringSsh = null; IsMonitoringActive = false; } }
    }

    private async Task RunMonitoringCycleStepAsync(ISshService localSsh, VpnProfile profile, bool isSingBox, bool isTrustTunnel, string serviceName, string ip, string displayCoreName, CancellationToken token)
    {
        var pingResult = await _monitorService.PingServerAsync(ip);
        PingMs = pingResult.Success ? pingResult.RoundtripTime : 0;

        var res = await _monitorService.GetResourcesAsync(localSsh, profile.CoreType);
        NetworkSpeed = res.NetworkSpeed; XrayProcesses = res.XrayProcesses; SynRecv = res.SynRecv; ErrorRate = res.ErrorRate;

        await UpdateSystemMetricsAsync(localSsh, res);

        int tcpCount = await GetTcpConnectionsCountAsync(localSsh, res.TcpConnections);
        TcpConnections = tcpCount;

        // ВНЕДРЕНО: Умный алгоритм с надежной проверкой (foolproof). 
        // systemctl is-active гарантированно возвращает статусы строго в порядке запроса (sb, xr, tt)
        string fallback = await localSsh.ExecuteCommandAsync("systemctl is-active sing-box xray trusttunnel 2>/dev/null");
        var fbLines = fallback.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        bool sbActive = fbLines.Length > 0 && fbLines[0].Trim() == "active";
        bool xrActive = fbLines.Length > 1 && fbLines[1].Trim() == "active";
        bool ttActive = fbLines.Length > 2 && fbLines[2].Trim() == "active";

        string actualDisplayCore = displayCoreName;
        if (sbActive && xrActive) actualDisplayCore = "Sing-box + Xray";
        else if (sbActive) actualDisplayCore = "Sing-box";
        else if (xrActive) actualDisplayCore = "Xray-core";

        if (ttActive && !actualDisplayCore.Contains("TrustTunnel"))
            actualDisplayCore = (actualDisplayCore == "TrustTunnel") ? "TrustTunnel" : actualDisplayCore + " + TrustTunnel";

        string coreStatusStr = "Stopped";
        if (actualDisplayCore.Contains("Sing-box") && sbActive) coreStatusStr = "Active";
        else if (actualDisplayCore.Contains("Xray") && xrActive) coreStatusStr = "Active";
        else if (actualDisplayCore.Contains("TrustTunnel") && ttActive) coreStatusStr = "Active";

        string journalLogs = await localSsh.ExecuteCommandAsync($"journalctl -u {serviceName} -n 5 --no-pager");
        string accessLogs = await GetAccessLogsAsync(localSsh, isSingBox, isTrustTunnel);
        string grepTest = await GetParserTestLogsAsync(localSsh, isSingBox, isTrustTunnel);

        var coreStats = await _monitorService.GetCoreStatusInfoAsync(localSsh, profile.CoreType);
        var allOnlineStats = await _monitorService.GetUserOnlineStatsAsync(localSsh, profile.CoreType);

        var activeUsernames = await GetActiveUsernamesAsync(localSsh, isSingBox, isTrustTunnel);
        var violationsBatch = await ProcessViolationsAsync(localSsh, isSingBox, isTrustTunnel, activeUsernames);

        var trafficStats = await CalculateTrafficStatsAsync(localSsh, isSingBox, isTrustTunnel, activeUsernames);

        var trafficBatch = new Dictionary<string, long>();
        var connectionBatch = new List<(string Email, string Ip, string Country)>();

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            UpdateUiAfterCycle(actualDisplayCore, coreStatusStr, coreStats, journalLogs, accessLogs, grepTest);

            if (SelectedServer != null)
            {
                // ФИКС: Умный алгоритм актуализации ядра в БД (foolproof)
                string detectedCoreType = sbActive ? "sing-box" : (ttActive ? "trusttunnel" : "xray");
                if (SelectedServer.CoreType != detectedCoreType)
                {
                    _logger.Log("MONITORING", $"[FOOLPROOF] Обнаружено расхождение ядра! БД: {SelectedServer.CoreType}, Реал: {detectedCoreType}. Обновляем...");
                    SelectedServer.CoreType = detectedCoreType;
                    _profileRepository.UpdateProfile(SelectedServer);
                }
            }

            bool dbNeedsUpdate = ProcessClientsAfterCycle(trafficStats, activeUsernames, allOnlineStats, trafficBatch, connectionBatch);

            if (dbNeedsUpdate && SelectedServer != null)
            {
                if (isSingBox) _ = _singBoxUserManager.SaveTrafficToDbAsync(ip, Clients);
                else if (isTrustTunnel) _ = _trustTunnelUserManager.SaveTrafficToDbAsync(ip, Clients);
                else _ = _userManager.SaveTrafficToDbAsync(ip, Clients);
            }

            TotalUsers = Clients.Count;
            ActiveUsers = Clients.Count(c => c.ActiveConnections > 0);
            TotalTraffic = FormatBytes(Clients.Sum(c => c.TrafficUsed));
        });

        _ = _analyticsService.SaveBatchAsync(ip, trafficBatch, connectionBatch, violationsBatch);
    }
}
