using CommunityToolkit.Mvvm.Input;
using System.Threading.Tasks;
using KoFFPanel.Domain.Entities;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;

namespace KoFFPanel.Presentation.ViewModels;

public partial class CabinetViewModel
{
    [RelayCommand]
    private async Task GenerateRealityConfigAsync()
    {
        if (_currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;
        ServerStatus = "Сброс ядра и генерация VLESS...";
        var result = await _xrayConfigurator.InitializeRealityAsync(_currentMonitoringSsh, SelectedServer.IpAddress);

        if (result.IsSuccess)
        {
            await _subscriptionService.InitializeServerAsync(_currentMonitoringSsh);
            await LoadUsersAsync();
            var admin = Clients.FirstOrDefault(c => c.Email == "Админ");
            if (admin != null)
            {
                admin.VlessLink = result.VlessLink;
                await _subscriptionService.UpdateUserSubscriptionAsync(_currentMonitoringSsh, admin.Uuid, result.VlessLink);
                System.Windows.Application.Current.Dispatcher.Invoke(() => {
                    System.Windows.Clipboard.SetText(_subscriptionService.GetSubscriptionUrl(SelectedServer.IpAddress, admin.Uuid));
                });
            }
            ServerStatus = "Онлайн (Сброс завершен, подписка в буфере!)";
        }
        else ServerStatus = $"ОШИБКА: {result.Message}";
    }

    [RelayCommand]
    private async Task AddClientAsync()
    {
        if (_currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;
        var window = _serviceProvider.GetRequiredService<Views.AddClientWindow>();
        if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;
        window.ShowDialog();

        if (window.DataContext is AddClientViewModel vm && vm.IsSuccess)
        {
            ServerStatus = $"Создание клиента {vm.ClientName}...";
            long limit = (long)vm.TrafficLimitGb * 1024 * 1024 * 1024;
            var (success, msg, vlessLink) = await _userManager.AddUserAsync(_currentMonitoringSsh, SelectedServer.IpAddress, vm.ClientName, limit, vm.ExpiryDate);

            if (success)
            {
                string uuid = vlessLink.Substring(8, 36);
                await _subscriptionService.UpdateUserSubscriptionAsync(_currentMonitoringSsh, uuid, vlessLink);
                ServerStatus = $"Онлайн (Клиент {vm.ClientName} добавлен!)";
                await LoadUsersAsync();
            }
            else ServerStatus = $"ОШИБКА: {msg}";
        }
    }

    [RelayCommand]
    private void OpenAnalytics(VpnClient? client)
    {
        if (client == null || SelectedServer == null) return;
        var window = _serviceProvider.GetRequiredService<Views.ClientAnalyticsWindow>();
        if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;

        if (window.DataContext is ClientAnalyticsViewModel vm)
        {
            vm.Initialize(SelectedServer.IpAddress, client.Email);
        }
        window.ShowDialog();
    }

    [RelayCommand]
    private async Task EditClientAsync(VpnClient? client)
    {
        if (client == null || _currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;
        var window = _serviceProvider.GetRequiredService<Views.AddClientWindow>();
        if (System.Windows.Application.Current.MainWindow != null) window.Owner = System.Windows.Application.Current.MainWindow;
        if (window.DataContext is AddClientViewModel vm) vm.LoadForEdit(client.Email, client.TrafficLimit, client.ExpiryDate, client.Note);
        window.ShowDialog();

        if (window.DataContext is AddClientViewModel resultVm && resultVm.IsSuccess)
        {
            ServerStatus = $"Обновление лимитов {client.Email}...";
            long newLimit = (long)resultVm.TrafficLimitGb * 1024 * 1024 * 1024;
            if (await _userManager.UpdateUserLimitsAsync(SelectedServer.IpAddress, client.Email, newLimit, resultVm.ExpiryDate))
            {
                client.TrafficLimit = newLimit; client.ExpiryDate = resultVm.ExpiryDate; client.Note = resultVm.Note;
                ServerStatus = $"Онлайн (Лимиты обновлены)";
            }
            else ServerStatus = "ОШИБКА: Не удалось обновить лимиты";
        }
    }

    [RelayCommand]
    private async Task DeleteClientAsync(VpnClient? client)
    {
        if (client == null || _currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;
        ServerStatus = $"Удаление {client.Email}...";
        var (success, msg) = await _userManager.RemoveUserAsync(_currentMonitoringSsh, SelectedServer.IpAddress, client.Email);
        System.Windows.Application.Current.Dispatcher.Invoke(() => Clients.Remove(client));
        if (success)
        {
            await _subscriptionService.DeleteUserSubscriptionAsync(_currentMonitoringSsh, client.Uuid);
            ServerStatus = $"Онлайн (Клиент удален)";
        }
        else ServerStatus = $"Ошибка: {msg}";
    }

    [RelayCommand]
    private void CopyClientLink(VpnClient? client)
    {
        if (client != null && SelectedServer != null)
        {
            System.Windows.Clipboard.SetText(_subscriptionService.GetSubscriptionUrl(SelectedServer.IpAddress, client.Uuid));
            ServerStatus = "HTTP-Подписка скопирована в буфер!";
        }
    }

    [RelayCommand]
    private async Task ResetClientTrafficAsync(VpnClient? client)
    {
        if (client == null || _currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;
        ServerStatus = $"Сброс трафика {client.Email}...";
        if (await _userManager.ResetTrafficAsync(_currentMonitoringSsh, client.Email))
        {
            client.TrafficUsed = 0; _previousTrafficStats[client.Email] = 0;
            await _userManager.SaveTrafficToDbAsync(SelectedServer.IpAddress, new[] { client });
            ServerStatus = "Онлайн (Трафик обнулен)";
        }
        else ServerStatus = "ОШИБКА: Сбой сброса";
    }

    [RelayCommand]
    private async Task ToggleClientAccessAsync(VpnClient? client)
    {
        if (client == null || _currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;
        bool newState = !client.IsActive; ServerStatus = $"{(newState ? "Разблокировка" : "Блокировка")} {client.Email}...";
        var (success, msg) = await _userManager.ToggleUserStatusAsync(_currentMonitoringSsh, SelectedServer.IpAddress, client.Email, newState);
        if (success)
        {
            client.IsActive = newState;
            if (newState && (client.Note?.StartsWith("ФРОД:") == true || client.Note == "Превышен лимит" || client.Note == "Истек срок")) client.Note = "";
            if (newState && _dailyIps.ContainsKey(client.Email)) _dailyIps[client.Email].Clear();
            ServerStatus = $"Онлайн ({client.Email} {(newState ? "разблокирован" : "заблокирован")})";
        }
        else ServerStatus = $"ОШИБКА: {msg}";
    }

    [RelayCommand] private void CopyXrayLogs() { if (!string.IsNullOrEmpty(XrayLogs)) { System.Windows.Clipboard.SetText(XrayLogs); ServerStatus = "Логи скопированы!"; } }
    [RelayCommand] private async Task RestartXrayAsync() { if (_currentMonitoringSsh != null && _currentMonitoringSsh.IsConnected) await _xrayService.RestartCoreAsync(_currentMonitoringSsh); }
    [RelayCommand] private async Task RebootServerAsync() { if (_currentMonitoringSsh != null && _currentMonitoringSsh.IsConnected) await _xrayService.RebootServerAsync(_currentMonitoringSsh); }
}