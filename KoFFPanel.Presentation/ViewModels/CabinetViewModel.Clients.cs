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
                await _subscriptionService.UpdateUserSubscriptionAsync(ssh, uuid, vlessLink);
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

    [RelayCommand]
    private void CopyClientLink(VpnClient? client)
    {
        var server = SelectedServer;
        if (client == null || server == null) return;

        string sni = server.Sni ?? "www.microsoft.com";
        string pubKey = server.PublicKey ?? "";
        string shortId = server.ShortId ?? "";
        string email = client.Email ?? "Unknown";
        string uuid = client.Uuid ?? "";
        string ip = server.IpAddress ?? "";
        int port = server.VpnPort > 0 ? server.VpnPort : 443;

        string clientJson = "";

        if (IsSingBoxActive())
        {
            clientJson = KoFFPanel.Application.Templates.SingBoxRealityConfigTemplate.GenerateClientConfig(ip, port, uuid, sni, pubKey, shortId);
        }
        else
        {
            clientJson = $$"""
            {
              "streamSettings": {
                "network": "tcp",
                "security": "reality",
                "realitySettings": {
                  "fingerprint": "chrome",
                  "serverName": "{{sni}}",
                  "publicKey": "{{pubKey}}",
                  "shortId": "{{shortId}}",
                  "spiderX": "/"
                }
              }
            }
            """;
        }

        var window = _serviceProvider.GetRequiredService<Views.ClientConfigWindow>();
        if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;

        if (window.DataContext is ClientConfigViewModel vm)
        {
            string httpLink = _subscriptionService.GetSubscriptionUrl(ip, uuid) ?? "";
            vm.Initialize(email, client.VlessLink ?? "", clientJson, httpLink);
        }

        window.ShowDialog();
    }

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
            vm.Initialize(client);

            // ИСПРАВЛЕНИЕ: Делаем Callback асинхронным, чтобы дождаться ответа ядра
            vm.SaveCallback = async (updatedClient) =>
            {
                string ip = server.IpAddress ?? "";

                // 1. Сохраняем текущее состояние трафика
                await _userManager.SaveTrafficToDbAsync(ip, Clients);

                // 2. Отправляем новые тумблеры в ядро (Ядро проверит и сбросит их в БД при ошибке)
                if (IsSingBoxActive())
                {
                    await _singBoxUserManager.SyncUsersToCoreAsync(_currentMonitoringSsh, Clients);
                }

                // 3. БРОНЕБОЙНЫЙ ФИКС UI: Мгновенно перезагружаем клиентов из БД!
                // Если ядро отменило Hysteria 2, UI вытянет из БД "false" и обновит таблицу в памяти.
                await LoadUsersAsync();

                ServerStatus = $"Онлайн (Протоколы для {updatedClient.Email} сохранены)";
            };
        }
        window.ShowDialog();
    }

    [RelayCommand] private void CopyXrayLogs() { if (!string.IsNullOrEmpty(XrayLogs)) System.Windows.Clipboard.SetText(XrayLogs); }
    [RelayCommand] private async Task RestartXrayAsync() { var ssh = _currentMonitoringSsh; if (ssh != null && ssh.IsConnected) await _xrayService.RestartCoreAsync(ssh); }
    [RelayCommand] private async Task RebootServerAsync() { var ssh = _currentMonitoringSsh; if (ssh != null && ssh.IsConnected) await _xrayService.RebootServerAsync(ssh); }
}