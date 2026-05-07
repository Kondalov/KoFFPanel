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

    // Добавляем переменную класса для отслеживания актуальной задачи
    private Guid _activeMonitoringTaskId;

    private async Task StartMonitoringLoopAsync(VpnProfile profile, CancellationToken token)
    {
        // Захват локального ID задачи для защиты от гонки потоков (Race Condition)
        var currentTaskId = Guid.NewGuid();
        _activeMonitoringTaskId = currentTaskId;

        System.Windows.Application.Current.Dispatcher.Invoke(() => IsMonitoringActive = true);

        while (!token.IsCancellationRequested)
        {
            UpdateStatusSecurely("Подключение...", currentTaskId);
            ISshService localSsh = _sshServiceFactory();
            _currentMonitoringSsh = localSsh;

            string ip = profile.IpAddress ?? "";
            string user = profile.Username ?? "root";
            string pass = profile.Password ?? "";
            string key = profile.KeyPath ?? "";

            _logger.Log("MONITORING", $"[START] Запуск цикла мониторинга. Ожидаемое Ядро: {profile.CoreType?.ToUpper()}");

            // ИСПРАВЛЕНИЕ: Добавлен жесткий таймаут коннекта на уровне Task, чтобы исключить зависание UI
            var connectTask = localSsh.ConnectAsync(ip, profile.Port, user, pass, key);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(15), token);

            if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask || await connectTask != "SUCCESS")
            {
                UpdateStatusSecurely("Ошибка соединения. Переподключение через 5с...", currentTaskId);
                if (_currentMonitoringSsh == localSsh) _currentMonitoringSsh = null;
                localSsh.Disconnect();

                try { await Task.Delay(5000, token); continue; }
                catch (TaskCanceledException) { break; }
            }

            UpdateStatusSecurely("Онлайн (Загрузка пользователей)", currentTaskId);
            await LoadUsersAsync();
            _ = _analyticsService.CleanupOldLogsAsync();

            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (!localSsh.IsConnected) throw new Exception("SSH сокет закрыт сервером");

                    await RunMonitoringCycleStepAsync(localSsh, profile, token);

                    // FOOLPROOF: Динамическое обновление статуса каждую итерацию. 
                    // Если цикл работает, пользователь всегда видит свежее время обновления.
                    UpdateStatusSecurely($"Онлайн (Синхр: {DateTime.Now:HH:mm:ss})", currentTaskId);

                    await Task.Delay(5000, token);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                UpdateStatusSecurely($"Связь потеряна. Реконнект...", currentTaskId);
                _logger.Log("MONITORING", $"Сбой: {ex.Message}");
            }
            finally
            {
                localSsh.Disconnect();
                if (_currentMonitoringSsh == localSsh) { _currentMonitoringSsh = null; }
            }

            if (token.IsCancellationRequested) break;

            try { await Task.Delay(3000, token); }
            catch (TaskCanceledException) { break; }
        }

        UpdateStatusSecurely("Оффлайн", currentTaskId);
        System.Windows.Application.Current.Dispatcher.Invoke(() => IsMonitoringActive = false);
    }

    // ИСПРАВЛЕНИЕ: Безопасное обновление UI, отклоняющее команды от убитых/старых потоков
    private void UpdateStatusSecurely(string status, Guid taskId)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (_activeMonitoringTaskId == taskId)
            {
                ServerStatus = status;
            }
        });
    }

    private async Task RunMonitoringCycleStepAsync(ISshService localSsh, VpnProfile profile, CancellationToken token)
    {
        string ip = profile.IpAddress ?? "";

        // ИСПРАВЛЕНИЕ: Определяем ядро динамически на каждом шаге цикла, 
        // чтобы изменения из БД (если они произошли) применялись мгновенно.
        bool isSingBox = profile.CoreType == "sing-box";
        bool isTrustTunnel = profile.CoreType == "trusttunnel";

        string displayCoreName = isSingBox ? "Sing-box" : (isTrustTunnel ? "TrustTunnel" : "Xray-core");
        if (profile.Inbounds.Any(i => i.Protocol.ToLower() == "trusttunnel") && !isTrustTunnel)
        {
            displayCoreName += " + TrustTunnel";
        }

        string serviceName = isSingBox ? "sing-box" : (isTrustTunnel ? "trusttunnel" : "xray");

        var pingResult = await _monitorService.PingServerAsync(ip);
        PingMs = pingResult.Success ? pingResult.RoundtripTime : 0;

        var res = await _monitorService.GetResourcesAsync(localSsh, profile.CoreType);
        NetworkSpeed = res.NetworkSpeed; XrayProcesses = res.XrayProcesses; SynRecv = res.SynRecv; ErrorRate = res.ErrorRate;

        await UpdateSystemMetricsAsync(localSsh, res);

        int tcpCount = await GetTcpConnectionsCountAsync(localSsh, res.TcpConnections);
        TcpConnections = tcpCount;

        string fallback = await localSsh.ExecuteCommandAsync("systemctl is-active sing-box xray trusttunnel 2>/dev/null");

        bool sbActive = false, xrActive = false, ttActive = false;

        // ИСПРАВЛЕНИЕ: Парсим статусы ТОЛЬКО если сервер реально ответил.
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            var fbLines = fallback.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            sbActive = fbLines.Length > 0 && fbLines[0].Trim() == "active";
            xrActive = fbLines.Length > 1 && fbLines[1].Trim() == "active";
            ttActive = fbLines.Length > 2 && fbLines[2].Trim() == "active";
        }

        string actualDisplayCore = displayCoreName;
        if (sbActive && xrActive) actualDisplayCore = "Sing-box + Xray";
        else if (sbActive) actualDisplayCore = "Sing-box";
        else if (xrActive) actualDisplayCore = "Xray-core";

        if (ttActive && !actualDisplayCore.Contains("TrustTunnel", StringComparison.OrdinalIgnoreCase))
            actualDisplayCore = string.Equals(actualDisplayCore, "TrustTunnel", StringComparison.OrdinalIgnoreCase) ? "TrustTunnel" : actualDisplayCore + " + TrustTunnel";

        string coreStatusStr = "Stopped";
        if (actualDisplayCore.Contains("Sing-box", StringComparison.OrdinalIgnoreCase) && sbActive) coreStatusStr = "Active";
        else if (actualDisplayCore.Contains("Xray", StringComparison.OrdinalIgnoreCase) && xrActive) coreStatusStr = "Active";
        else if (actualDisplayCore.Contains("TrustTunnel", StringComparison.OrdinalIgnoreCase) && ttActive) coreStatusStr = "Active";

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

            // ИСПРАВЛЕНИЕ: Если fallback пустой (нет связи), мы НЕ перезаписываем ядро!
            if (SelectedServer != null && !string.IsNullOrWhiteSpace(fallback))
            {
                string detectedCoreType = SelectedServer.CoreType; // Берем текущее как дефолт
                if (sbActive) detectedCoreType = "sing-box";
                else if (xrActive) detectedCoreType = "xray";
                else if (ttActive) detectedCoreType = "trusttunnel";

                // Обновляем ядро в БД ТОЛЬКО если 100% подтверждено наличие хотя бы одного активного процесса
                if (SelectedServer.CoreType != detectedCoreType && (sbActive || xrActive || ttActive))
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
