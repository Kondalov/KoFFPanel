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
    private bool IsSingBoxActive() => CoreTitleLabel != null && CoreTitleLabel.Contains("Sing-box", StringComparison.OrdinalIgnoreCase);

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

                    var (success, msg, vlessLink) = IsSingBoxActive()
                        ? await _singBoxUserManager.AddUserAsync(ssh, ip, vm.ClientName, limit, vm.ExpiryDate, vm.IsP2PBlocked)
                        : await _userManager.AddUserAsync(ssh, ip, vm.ClientName, limit, vm.ExpiryDate);

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
        ServerStatus = $"{(newState ? "Активация" : "Деактивация")} {email}...";

        var (success, msg) = IsSingBoxActive()
            ? await _singBoxUserManager.ToggleUserStatusAsync(ssh, ip, email, newState)
            : await _userManager.ToggleUserStatusAsync(ssh, ip, email, newState);

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
                vm.LoadForEdit(email, client.TrafficLimit, client.ExpiryDate, client.Note ?? "", client.IsP2PBlocked);

                // Открываем окно
                window.ShowDialog();

                // === ИСПРАВЛЕНИЕ: Проверяем флаг успешности вместо DialogResult ===
                if (vm.IsSuccess)
                {
                    long newLimit = (long)(vm.TrafficLimitGb * 1024L * 1024 * 1024);

                    bool success = IsSingBoxActive()
                        ? await _singBoxUserManager.UpdateUserLimitsAsync(ssh, ip, email, newLimit, vm.ExpiryDate, vm.IsP2PBlocked)
                        : await _userManager.UpdateUserLimitsAsync(ip, email, newLimit, vm.ExpiryDate);

                    if (success)
                    {
                        // Обновляем модель в UI
                        client.TrafficLimit = newLimit;
                        client.ExpiryDate = vm.ExpiryDate;
                        client.Note = vm.Note;
                        client.IsP2PBlocked = vm.IsP2PBlocked;

                        // === ИСПРАВЛЕНИЕ: Принудительное сохранение изменений в БД ===
                        var dbContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
                        var dbClient = dbContext.Clients.FirstOrDefault(c => c.Email == email && c.ServerIp == ip);
                        if (dbClient != null)
                        {
                            dbClient.TrafficLimit = newLimit;
                            dbClient.ExpiryDate = vm.ExpiryDate;
                            dbClient.Note = vm.Note;
                            dbClient.IsP2PBlocked = vm.IsP2PBlocked;
                            await dbContext.SaveChangesAsync();
                        }

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
                await _userManager.SaveTrafficToDbAsync(ip, Clients);

                if (IsSingBoxActive() && _currentMonitoringSsh != null)
                {
                    await _singBoxUserManager.SyncUsersToCoreAsync(_currentMonitoringSsh, Clients);
                }

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
            };
        }
        window.ShowDialog();
    }
}
