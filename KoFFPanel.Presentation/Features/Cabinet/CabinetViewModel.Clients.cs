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
using Microsoft.EntityFrameworkCore;

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
                window.ShowDialog();

                if (vm.IsSuccess)
                {
                    ServerStatus = $"Добавление клиента {vm.ClientName}...";
                    long limit = (long)(vm.TrafficLimitGb * 1024L * 1024 * 1024);
                    string ip = server.IpAddress ?? "";

                    bool success; string msg; string vlessLink;

                    if (IsSingBoxActive())
                    {
                        (success, msg, vlessLink) = await _singBoxUserManager.AddUserAsync(ssh, ip, vm.ClientName, limit, vm.ExpiryDate, vm.IsP2PBlocked, vm.IsVlessEnabled, vm.IsHysteria2Enabled, vm.IsTrustTunnelEnabled, vm.IsTrojanEnabled, vm.IsShadowsocksEnabled);
                    }
                    else if (IsTrustTunnelActive())
                    {
                        (success, msg, vlessLink) = await _trustTunnelUserManager.AddUserAsync(ssh, ip, vm.ClientName, limit, vm.ExpiryDate, vm.IsP2PBlocked);
                    }
                    else
                    {
                        (success, msg, vlessLink) = await _userManager.AddUserAsync(ssh, ip, vm.ClientName, limit, vm.ExpiryDate, vm.IsP2PBlocked, vm.IsVlessEnabled, vm.IsHysteria2Enabled, vm.IsTrustTunnelEnabled, vm.IsTrojanEnabled, vm.IsShadowsocksEnabled);
                    }

                    if (success)
                    {
                        var freshContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
                        var freshClient = await freshContext.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Email == vm.ClientName && c.ServerIp == ip);

                        if (freshClient != null)
                        {
                            var activeLinks = new List<string>();
                            if (freshClient.IsVlessEnabled && !string.IsNullOrEmpty(freshClient.VlessLink)) activeLinks.Add(freshClient.VlessLink);
                            if (freshClient.IsHysteria2Enabled && !string.IsNullOrEmpty(freshClient.Hysteria2Link)) activeLinks.Add(freshClient.Hysteria2Link);
                            if (freshClient.IsTrojanEnabled && !string.IsNullOrEmpty(freshClient.TrojanLink)) activeLinks.Add(freshClient.TrojanLink);
                            if (freshClient.IsShadowsocksEnabled && !string.IsNullOrEmpty(freshClient.ShadowsocksLink)) activeLinks.Add(freshClient.ShadowsocksLink);
                            if (freshClient.IsTrustTunnelEnabled && !string.IsNullOrEmpty(freshClient.TrustTunnelLink)) activeLinks.Add(freshClient.TrustTunnelLink);

                            await _subscriptionService.UpdateUserSubscriptionAsync(ssh, freshClient.Uuid ?? "", activeLinks);
                        }

                        ServerStatus = $"Онлайн (Клиент {vm.ClientName} добавлен!)";
                        await LoadUsersAsync();
                    }
                    else ServerStatus = $"Ошибка: {msg}";
                }
            }
        }
        catch (Exception ex) { ServerStatus = "Ошибка приложения."; }
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

        try
        {
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
            else
            {
                ServerStatus = $"Ошибка удаления: {msg}";
            }
        }
        catch (Exception ex)
        {
            _logger.Log("CLIENT-DELETE-ERR", $"Критический сбой: {ex.Message}");
            ServerStatus = "Сбой связи при удалении.";
        }
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

        try
        {
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
            else
            {
                ServerStatus = $"Ошибка: {msg}";
            }
        }
        catch (System.Text.Json.JsonException jsonEx)
        {
            _logger.Log("CLIENT-TOGGLE-ERR", $"Ошибка парсинга конфига (возможно сервер не вернул данные): {jsonEx.Message}");
            ServerStatus = "Сбой чтения конфига. Повторите попытку.";
        }
        catch (Exception ex)
        {
            _logger.Log("CLIENT-TOGGLE-ERR", $"Критический сбой SSH: {ex.Message}");
            ServerStatus = "Обрыв связи с сервером.";
        }
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

            if (window.DataContext is AddClientViewModel vm)
            {
                vm.LoadForEdit(client.Email ?? "", client.TrafficLimit, client.ExpiryDate, client.Note ?? "", client.IsP2PBlocked, client.IsVlessEnabled, client.IsHysteria2Enabled, client.IsTrustTunnelEnabled, client.IsTrojanEnabled, client.IsShadowsocksEnabled);
                window.ShowDialog();

                if (vm.IsSuccess)
                {
                    long newLimit = (long)(vm.TrafficLimitGb * 1024L * 1024 * 1024);
                    string email = client.Email ?? ""; string ip = server.IpAddress ?? "";
                    bool success;

                    if (IsSingBoxActive()) success = await _singBoxUserManager.UpdateUserLimitsAsync(ssh, ip, email, newLimit, vm.ExpiryDate, vm.Note, vm.IsP2PBlocked, vm.IsVlessEnabled, vm.IsHysteria2Enabled, vm.IsTrustTunnelEnabled, vm.IsTrojanEnabled, vm.IsShadowsocksEnabled);
                    else if (IsTrustTunnelActive()) success = await _trustTunnelUserManager.UpdateUserLimitsAsync(ssh, ip, email, newLimit, vm.ExpiryDate, vm.Note, vm.IsP2PBlocked);
                    else success = await _userManager.UpdateUserLimitsAsync(ssh, ip, email, newLimit, vm.ExpiryDate, vm.Note, vm.IsP2PBlocked, vm.IsVlessEnabled, vm.IsHysteria2Enabled, vm.IsTrustTunnelEnabled, vm.IsTrojanEnabled, vm.IsShadowsocksEnabled);

                    if (success)
                    {
                        client.TrafficLimit = newLimit; client.ExpiryDate = vm.ExpiryDate; client.Note = vm.Note;
                        client.IsP2PBlocked = vm.IsP2PBlocked; client.IsVlessEnabled = vm.IsVlessEnabled;
                        client.IsHysteria2Enabled = vm.IsHysteria2Enabled; client.IsTrustTunnelEnabled = vm.IsTrustTunnelEnabled;
                        client.IsTrojanEnabled = vm.IsTrojanEnabled; client.IsShadowsocksEnabled = vm.IsShadowsocksEnabled;

                        var freshContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
                        var freshClient = await freshContext.Clients.AsNoTracking().FirstOrDefaultAsync(c => c.Uuid == client.Uuid);

                        if (freshClient != null)
                        {
                            var activeLinks = new List<string>();
                            if (freshClient.IsVlessEnabled && !string.IsNullOrEmpty(freshClient.VlessLink)) activeLinks.Add(freshClient.VlessLink);
                            if (freshClient.IsHysteria2Enabled && !string.IsNullOrEmpty(freshClient.Hysteria2Link)) activeLinks.Add(freshClient.Hysteria2Link);
                            if (freshClient.IsTrojanEnabled && !string.IsNullOrEmpty(freshClient.TrojanLink)) activeLinks.Add(freshClient.TrojanLink);
                            if (freshClient.IsShadowsocksEnabled && !string.IsNullOrEmpty(freshClient.ShadowsocksLink)) activeLinks.Add(freshClient.ShadowsocksLink);
                            if (freshClient.IsTrustTunnelEnabled && !string.IsNullOrEmpty(freshClient.TrustTunnelLink)) activeLinks.Add(freshClient.TrustTunnelLink);

                            await _subscriptionService.UpdateUserSubscriptionAsync(ssh, freshClient.Uuid ?? "", activeLinks);
                        }

                        ServerStatus = "Онлайн (Лимиты обновлены)";
                    }
                    else ServerStatus = "Ошибка обновления лимитов.";
                }
            }
        }
        catch (Exception ex) { ServerStatus = "Ошибка приложения."; }
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
            vm.Initialize(client, _subscriptionService.GetSubscriptionUrl(server.IpAddress ?? "", client.Uuid ?? ""));

            vm.SaveCallback = async (updatedClient) =>
            {
                ServerStatus = $"Сохранение настроек {updatedClient.Email}...";
                bool syncSuccess = IsSingBoxActive() && _currentMonitoringSsh != null ? await _singBoxUserManager.SyncUsersToCoreAsync(_currentMonitoringSsh, Clients) :
                                   (_currentMonitoringSsh != null ? await _userManager.SyncUsersToCoreAsync(_currentMonitoringSsh, Clients) : false);

                if (syncSuccess)
                {
                    var freshContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
                    var freshClient = freshContext.Clients.AsNoTracking().FirstOrDefault(c => c.Uuid == updatedClient.Uuid);

                    if (freshClient != null && _currentMonitoringSsh != null && _currentMonitoringSsh.IsConnected)
                    {
                        var activeLinks = new List<string>();
                        if (freshClient.IsVlessEnabled && !string.IsNullOrEmpty(freshClient.VlessLink)) activeLinks.Add(freshClient.VlessLink);
                        if (freshClient.IsHysteria2Enabled && !string.IsNullOrEmpty(freshClient.Hysteria2Link)) activeLinks.Add(freshClient.Hysteria2Link);
                        if (freshClient.IsTrustTunnelEnabled && !string.IsNullOrEmpty(freshClient.TrustTunnelLink)) activeLinks.Add(freshClient.TrustTunnelLink);
                        // ИСПРАВЛЕНИЕ: Добавлены Trojan и Shadowsocks
                        if (freshClient.IsTrojanEnabled && !string.IsNullOrEmpty(freshClient.TrojanLink)) activeLinks.Add(freshClient.TrojanLink);
                        if (freshClient.IsShadowsocksEnabled && !string.IsNullOrEmpty(freshClient.ShadowsocksLink)) activeLinks.Add(freshClient.ShadowsocksLink);

                        await _subscriptionService.UpdateUserSubscriptionAsync(_currentMonitoringSsh, freshClient.Uuid ?? "", activeLinks);
                    }

                    await LoadUsersAsync();
                    ServerStatus = $"Онлайн (Настройки сохранены)";
                }
                else ServerStatus = "Ошибка синхронизации с ядром.";
            };
        }
        window.ShowDialog();
    }
}
