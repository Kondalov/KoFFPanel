using KoFFPanel.Domain.Entities;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Presentation.Features.Cabinet;
using KoFFPanel.Presentation.Features.Bot;
using KoFFPanel.Presentation.Features.Terminal;
using KoFFPanel.Presentation.Features.Deploy;
using KoFFPanel.Presentation.Features.Analytics;
using KoFFPanel.Presentation.Features.Management;
using KoFFPanel.Presentation.Features.Config;
using KoFFPanel.Presentation.Features.Shared.Dialogs;
using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;

namespace KoFFPanel.Presentation.Features.Cabinet;

public partial class CabinetViewModel
{
    private bool IsSingBoxActive() => SelectedServer?.CoreType == "sing-box";
    private bool IsTrustTunnelActive() => SelectedServer?.CoreType == "trusttunnel";

    [RelayCommand]
    private async Task GenerateRealityConfigAsync() { await Task.CompletedTask; }

    [RelayCommand]
    private async Task AddClientAsync()
    {
        var ssh = _currentMonitoringSsh;
        var server = SelectedServer;
        if (ssh == null || !ssh.IsConnected || server == null) return;

        try
        {
            var window = _serviceProvider.GetRequiredService<AddClientWindow>();
            if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;

            if (window.DataContext is AddClientViewModel vm)
            {
                vm.Initialize(server.IpAddress ?? "");

                // Открываем окно
                window.ShowDialog();

                // === ИСПРАВЛЕНИЕ: Проверяем флаг успешности вместо DialogResult ===
                if (vm.IsSuccess)
                {
                    ServerStatus = $"Добавление клиента {vm.ClientName}...";
                    long limit = (long)(vm.TrafficLimitGb * 1024L * 1024 * 1024);
                    string ip = server.IpAddress ?? "";

                    bool success; string msg; string vlessLink;

                    if (IsSingBoxActive())
                    {
                        (success, msg, vlessLink) = await _singBoxUserManager.AddUserAsync(ssh, ip, vm.ClientName, limit, vm.ExpiryDate, vm.IsP2PBlocked, vm.IsVlessEnabled, vm.IsHysteria2Enabled, vm.IsTrustTunnelEnabled);
                    }
                    else if (IsTrustTunnelActive())
                    {
                        (success, msg, vlessLink) = await _trustTunnelUserManager.AddUserAsync(ssh, ip, vm.ClientName, limit, vm.ExpiryDate, vm.IsP2PBlocked);
                    }
                    else
                    {
                        (success, msg, vlessLink) = await _userManager.AddUserAsync(ssh, ip, vm.ClientName, limit, vm.ExpiryDate, vm.IsP2PBlocked);
                    }

                    if (success)
                    {
                        var links = new List<string>();
                        if (!string.IsNullOrEmpty(vlessLink)) links.Add(vlessLink);
                        await _subscriptionService.UpdateUserSubscriptionAsync(ssh, vm.ClientName, links);

                        ServerStatus = $"Онлайн (Клиент {vm.ClientName} добавлен!)";
                        await LoadUsersAsync();
                    }
                    else
                    {
                        ServerStatus = $"Ошибка: {msg}";
                        _logger?.Log("ADD-CLIENT", $"Ошибка создания на сервере: {msg}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ServerStatus = "Ошибка приложения при добавлении.";
            _logger?.Log("ADD-CLIENT-CRASH", ex.Message);
        }
    }

    [RelayCommand]
    private async Task DeleteClientAsync(VpnClient? client)
    {
        var ssh = _currentMonitoringSsh;
        var server = SelectedServer;
        if (client == null || ssh == null || !ssh.IsConnected || server == null) return;

        string email = client.Email ?? "Unknown";
        string uuid = client.Uuid ?? "";
        string ip = server.IpAddress ?? "";
        ServerStatus = $"Удаление {email}...";

        bool success; string msg;
        if (IsSingBoxActive()) (success, msg) = await _singBoxUserManager.RemoveUserAsync(ssh, ip, email);
        else if (IsTrustTunnelActive()) (success, msg) = await _trustTunnelUserManager.RemoveUserAsync(ssh, ip, email);
        else (success, msg) = await _userManager.RemoveUserAsync(ssh, ip, email);

        if (success)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => Clients.Remove(client));
            await _subscriptionService.DeleteUserSubscriptionAsync(ssh, uuid);

            ServerStatus = $"Онлайн (Клиент {email} успешно удален)";
        }
        else ServerStatus = $"Ошибка удаления: {msg}";
    }

    [RelayCommand]
    private async Task ToggleClientAccessAsync(VpnClient? client)
    {
        var ssh = _currentMonitoringSsh;
        var server = SelectedServer;
        if (client == null || ssh == null || !ssh.IsConnected || server == null) return;

        bool newState = !client.IsActive;
        string email = client.Email ?? "Unknown";
        string ip = server.IpAddress ?? "";
        ServerStatus = $"{(newState ? "Активация" : "Деактивация")} {email}...";

        bool success; string msg;
        if (IsSingBoxActive()) (success, msg) = await _singBoxUserManager.ToggleUserStatusAsync(ssh, ip, email, newState);
        else if (IsTrustTunnelActive()) (success, msg) = await _trustTunnelUserManager.ToggleUserStatusAsync(ssh, ip, email, newState);
        else (success, msg) = await _userManager.ToggleUserStatusAsync(ssh, ip, email, newState);

        if (success)
        {
            client.IsActive = newState;
            if (newState && (client.Note?.StartsWith("ФРОД:") == true || client.Note == "Превышен лимит" || client.Note == "Истек срок")) client.Note = "";
            ServerStatus = $"Онлайн ({email} {(newState ? "активирован" : "отключен")})";
        }
        else ServerStatus = $"Ошибка: {msg}";
    }

    [RelayCommand]
    private async Task EditClientAsync(VpnClient? client)
    {
        var ssh = _currentMonitoringSsh;
        var server = SelectedServer;
        if (client == null || ssh == null || !ssh.IsConnected || server == null) return;

        try
        {
            var window = _serviceProvider.GetRequiredService<AddClientWindow>();
            if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;

            string email = client.Email ?? "Unknown";
            string ip = server.IpAddress ?? "";

            if (window.DataContext is AddClientViewModel vm)
            {
                vm.LoadForEdit(email, client.TrafficLimit, client.ExpiryDate, client.Note ?? "", client.IsP2PBlocked, client.IsVlessEnabled, client.IsHysteria2Enabled, client.IsTrustTunnelEnabled);

                // Открываем окно
                window.ShowDialog();

                // === ИСПРАВЛЕНИЕ: Проверяем флаг успешности вместо DialogResult ===
                if (vm.IsSuccess)
                {
                    long newLimit = (long)(vm.TrafficLimitGb * 1024L * 1024 * 1024);

                    bool success;
                    if (IsSingBoxActive())
                    {
                        success = await _singBoxUserManager.UpdateUserLimitsAsync(ssh, ip, email, newLimit, vm.ExpiryDate, vm.IsP2PBlocked, vm.IsVlessEnabled, vm.IsHysteria2Enabled, vm.IsTrustTunnelEnabled);
                    }
                    else if (IsTrustTunnelActive())
                    {
                        success = await _trustTunnelUserManager.UpdateUserLimitsAsync(ssh, ip, email, newLimit, vm.ExpiryDate, vm.IsP2PBlocked);
                    }
                    else
                    {
                        success = await _userManager.UpdateUserLimitsAsync(ssh, ip, email, newLimit, vm.ExpiryDate, vm.IsP2PBlocked, vm.IsVlessEnabled, vm.IsHysteria2Enabled, vm.IsTrustTunnelEnabled);
                    }

                    if (success)
                    {
                        // Обновляем модель в UI
                        client.TrafficLimit = newLimit;
                        client.ExpiryDate = vm.ExpiryDate;
                        client.Note = vm.Note;
                        client.IsP2PBlocked = vm.IsP2PBlocked;
                        client.IsVlessEnabled = vm.IsVlessEnabled;
                        client.IsHysteria2Enabled = vm.IsHysteria2Enabled;
                        client.IsTrustTunnelEnabled = vm.IsTrustTunnelEnabled;

                        ServerStatus = "Онлайн (Лимиты обновлены)";
                    }
                    else
                    {
                        ServerStatus = "Ошибка обновления лимитов.";
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ServerStatus = "Ошибка приложения при редактировании.";
            _logger?.Log("EDIT-CLIENT-CRASH", ex.Message);
        }
    }

    [RelayCommand]
    private async Task ResetClientTrafficAsync(VpnClient? client)
    {
        var ssh = _currentMonitoringSsh;
        var server = SelectedServer;
        if (client == null || ssh == null || !ssh.IsConnected || server == null) return;

        string email = client.Email ?? "";
        string ip = server.IpAddress ?? "";

        if (IsSingBoxActive()) await _singBoxUserManager.ResetTrafficAsync(ssh, email);
        else await _userManager.ResetTrafficAsync(ssh, email);

        client.TrafficUsed = 0; _previousTrafficStats[email] = 0;
        await _userManager.SaveTrafficToDbAsync(ip, new[] { client });
    }

    [RelayCommand]
    private void OpenAnalytics(VpnClient? client)
    {
        var server = SelectedServer;
        if (client == null || server == null) return;

        var window = _serviceProvider.GetRequiredService<ClientAnalyticsWindow>();
        if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;
        if (window.DataContext is ClientAnalyticsViewModel vm) vm.Initialize(server.IpAddress ?? "", client.Email ?? "");
        window.Show();
    }

    [RelayCommand]
    private void OpenProtocols(VpnClient? client)
    {
        var server = SelectedServer;
        if (client == null || server == null) return;

        var window = _serviceProvider.GetRequiredService<ClientProtocolsWindow>();
        if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;

        if (window.DataContext is ClientProtocolsViewModel vm)
        {
            string ip = server.IpAddress ?? "";
            vm.Initialize(client, _subscriptionService.GetSubscriptionUrl(ip, client.Uuid ?? ""));

            vm.SaveCallback = async (updatedClient) =>
            {
                ServerStatus = $"Сохранение настроек {updatedClient.Email}...";
                string ip = server.IpAddress ?? "";
                
                // === 2026 MODERNIZATION: Полная синхронизация ===
                // Передаем весь список клиентов в менеджер, он сам обновит БД и конфиг ядра
                bool syncSuccess;
                if (IsSingBoxActive() && _currentMonitoringSsh != null)
                {
                    syncSuccess = await _singBoxUserManager.SyncUsersToCoreAsync(_currentMonitoringSsh, Clients);
                }
                else if (_currentMonitoringSsh != null)
                {
                    syncSuccess = await _userManager.SyncUsersToCoreAsync(_currentMonitoringSsh, Clients);
                }
                else syncSuccess = false;

                if (syncSuccess)
                {
                    var activeLinks = new List<string>();
                    if (updatedClient.IsVlessEnabled && !string.IsNullOrEmpty(updatedClient.VlessLink) && updatedClient.VlessLink.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) activeLinks.Add(updatedClient.VlessLink);
                    if (updatedClient.IsHysteria2Enabled && !string.IsNullOrEmpty(updatedClient.Hysteria2Link) && updatedClient.Hysteria2Link.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)) activeLinks.Add(updatedClient.Hysteria2Link);
                    if (updatedClient.IsTrustTunnelEnabled && !string.IsNullOrEmpty(updatedClient.TrustTunnelLink) && updatedClient.TrustTunnelLink.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) activeLinks.Add(updatedClient.TrustTunnelLink);

                    if (_currentMonitoringSsh != null && _currentMonitoringSsh.IsConnected)
                    {
                        await _subscriptionService.UpdateUserSubscriptionAsync(_currentMonitoringSsh, updatedClient.Uuid ?? "", activeLinks);
                    }

                    await LoadUsersAsync();
                    ServerStatus = $"Онлайн (Настройки для {updatedClient.Email} сохранены)";
                }
                else
                {
                    ServerStatus = "Ошибка синхронизации с ядром.";
                }
            };
        }
        window.ShowDialog();
    }
}
