using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using KoFFPanel.Domain.Entities;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using System;
using KoFFPanel.Application.Interfaces;

namespace KoFFPanel.Presentation.ViewModels;

public partial class CabinetViewModel
{
    private bool IsSingBoxActive() => CoreTitleLabel != null && CoreTitleLabel.Contains("Sing-box", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private async Task GenerateRealityConfigAsync() { await Task.CompletedTask; }

    [RelayCommand]
    private async Task AddClientAsync()
    {
        var ssh = _currentMonitoringSsh;
        var server = SelectedServer;
        if (ssh == null || !ssh.IsConnected || server == null) return;

        var window = _serviceProvider.GetRequiredService<Views.AddClientWindow>();
        if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();

        if (window.DataContext is AddClientViewModel vm && vm.IsSuccess)
        {
            ServerStatus = $"Создание клиента {vm.ClientName}...";
            long limit = (long)vm.TrafficLimitGb * 1024 * 1024 * 1024;
            string ip = server.IpAddress ?? "";

            var (success, msg, vlessLink) = IsSingBoxActive()
                ? await _singBoxUserManager.AddUserAsync(ssh, ip, vm.ClientName, limit, vm.ExpiryDate, vm.IsP2PBlocked)
                : await _userManager.AddUserAsync(ssh, ip, vm.ClientName, limit, vm.ExpiryDate);

            if (success)
            {
                string uuid = vlessLink.Substring(8, 36);
                await _subscriptionService.UpdateUserSubscriptionAsync(ssh, uuid, new[] { vlessLink });
                ServerStatus = $"Онлайн (Клиент {vm.ClientName} добавлен!)";
                await LoadUsersAsync();
            }
            else ServerStatus = $"ОШИБКА: {msg}";
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

        var (success, msg) = IsSingBoxActive()
            ? await _singBoxUserManager.RemoveUserAsync(ssh, ip, email)
            : await _userManager.RemoveUserAsync(ssh, ip, email);

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
        ServerStatus = $"{(newState ? "Разблокировка" : "Блокировка")} {email}...";

        var (success, msg) = IsSingBoxActive()
            ? await _singBoxUserManager.ToggleUserStatusAsync(ssh, ip, email, newState)
            : await _userManager.ToggleUserStatusAsync(ssh, ip, email, newState);

        if (success)
        {
            client.IsActive = newState;
            if (newState && (client.Note?.StartsWith("ФРОД:") == true || client.Note == "Превышен лимит" || client.Note == "Истек срок")) client.Note = "";
            ServerStatus = $"Онлайн ({email} {(newState ? "разблокирован" : "заблокирован")})";
        }
        else ServerStatus = $"ОШИБКА: {msg}";
    }

    // ВНИМАНИЕ: Метод CopyClientLink ПОЛНОСТЬЮ УДАЛЕН. Вызовы старого окна больше не нужны.

    [RelayCommand]
    private async Task EditClientAsync(VpnClient? client)
    {
        var ssh = _currentMonitoringSsh;
        var server = SelectedServer;
        if (client == null || ssh == null || !ssh.IsConnected || server == null) return;

        var window = _serviceProvider.GetRequiredService<Views.AddClientWindow>();
        if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;

        string email = client.Email ?? "Unknown";
        string ip = server.IpAddress ?? "";

        if (window.DataContext is AddClientViewModel vm) vm.LoadForEdit(email, client.TrafficLimit, client.ExpiryDate, client.Note ?? "", client.IsP2PBlocked);
        window.ShowDialog();

        if (window.DataContext is AddClientViewModel resultVm && resultVm.IsSuccess)
        {
            long newLimit = (long)resultVm.TrafficLimitGb * 1024 * 1024 * 1024;

            bool success = IsSingBoxActive()
                ? await _singBoxUserManager.UpdateUserLimitsAsync(ssh, ip, email, newLimit, resultVm.ExpiryDate, resultVm.IsP2PBlocked)
                : await _userManager.UpdateUserLimitsAsync(ip, email, newLimit, resultVm.ExpiryDate);

            if (success)
            {
                client.TrafficLimit = newLimit;
                client.ExpiryDate = resultVm.ExpiryDate;
                client.Note = resultVm.Note;
                client.IsP2PBlocked = resultVm.IsP2PBlocked;
            }
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

        var window = _serviceProvider.GetRequiredService<Views.ClientAnalyticsWindow>();
        if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;
        if (window.DataContext is ClientAnalyticsViewModel vm) vm.Initialize(server.IpAddress ?? "", client.Email ?? "");
        window.ShowDialog();
    }

    [RelayCommand]
    private void OpenProtocols(VpnClient? client)
    {
        var server = SelectedServer;
        if (client == null || server == null) return;

        var window = _serviceProvider.GetRequiredService<Views.ClientProtocolsWindow>();
        if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;

        if (window.DataContext is ClientProtocolsViewModel vm)
        {
            // Генерируем HTTP-подписку и передаем ее в новое окно
            string ip = server.IpAddress ?? "";
            string httpLink = _subscriptionService.GetSubscriptionUrl(ip, client.Uuid ?? "") ?? "";

            vm.Initialize(client, httpLink);

            vm.SaveCallback = async (updatedClient) =>
            {
                await _userManager.SaveTrafficToDbAsync(ip, Clients);

                if (IsSingBoxActive())
                {
                    await _singBoxUserManager.SyncUsersToCoreAsync(_currentMonitoringSsh, Clients);
                }

                // === ИСПРАВЛЕНИЕ: Умное обновление файла HTTP-подписки ===
                var activeLinks = new System.Collections.Generic.List<string>();

                if (updatedClient.IsVlessEnabled && !string.IsNullOrEmpty(updatedClient.VlessLink))
                {
                    activeLinks.Add(updatedClient.VlessLink);
                }

                if (updatedClient.IsHysteria2Enabled && !string.IsNullOrEmpty(updatedClient.Hysteria2Link))
                {
                    activeLinks.Add(updatedClient.Hysteria2Link);
                }

                if (updatedClient.IsTrustTunnelEnabled && !string.IsNullOrEmpty(updatedClient.TrustTunnelLink))
                {
                    activeLinks.Add(updatedClient.TrustTunnelLink);
                }

                if (_currentMonitoringSsh != null && _currentMonitoringSsh.IsConnected)
                {
                    await _subscriptionService.UpdateUserSubscriptionAsync(_currentMonitoringSsh, updatedClient.Uuid ?? "", activeLinks);
                }
                // =========================================================

                await LoadUsersAsync();

                ServerStatus = $"Онлайн (Протоколы для {updatedClient.Email} сохранены)";
            };
        }
        window.ShowDialog();
    }
}