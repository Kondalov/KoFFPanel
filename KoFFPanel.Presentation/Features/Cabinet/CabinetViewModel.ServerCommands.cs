using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KoFFPanel.Domain.Entities;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using KoFFPanel.Presentation.Features.Management;
using KoFFPanel.Presentation.Features.Deploy;
using KoFFPanel.Presentation.Features.Terminal;

namespace KoFFPanel.Presentation.Features.Cabinet;

public partial class CabinetViewModel
{
    [RelayCommand] 
    private void AddServer() 
    { 
        var w = _serviceProvider.GetRequiredService<AddServerWindow>(); 
        w.ShowDialog(); 
        LoadData(); 
    }

    [RelayCommand] 
    private void DeleteServer(VpnProfile? p) 
    { 
        if (p == null) return; 
        _profileRepository.DeleteProfile(p.Id); 
        if (SelectedServer?.Id == p.Id) SelectedServer = null; 
        LoadData(); 
    }

    [RelayCommand] 
    private void EditServer(VpnProfile? p) 
    { 
        if (p == null) return; 
        var w = _serviceProvider.GetRequiredService<AddServerWindow>(); 
        if (System.Windows.Application.Current.MainWindow != null) w.Owner = System.Windows.Application.Current.MainWindow; 
        if (w.DataContext is AddServerViewModel vm) vm.LoadForEdit(p); 
        w.ShowDialog(); 
        LoadData(); 
    }

    [RelayCommand] 
    private void OpenDeployWizard() 
    { 
        if (SelectedServer == null) return; 
        var w = _serviceProvider.GetRequiredService<DeployWizardWindow>(); 
        if (System.Windows.Application.Current.MainWindow != null) w.Owner = System.Windows.Application.Current.MainWindow; 
        if (w.DataContext is DeployWizardViewModel vm) 
        { 
            vm.OnInstallRequested = OpenTerminalWithCommand; 
            _ = vm.InitializeAsync(SelectedServer); 
        } 
        w.ShowDialog(); 
    }

    [RelayCommand] 
    private void OpenTerminal() 
    { 
        if (SelectedServer == null) return; 
        OpenTerminalWithCommand(""); 
    }

    private void OpenTerminalWithCommand(string command) 
    { 
        if (SelectedServer == null) return; 
        var w = _serviceProvider.GetRequiredService<TerminalWindow>(); 
        if (System.Windows.Application.Current.MainWindow != null) w.Owner = System.Windows.Application.Current.MainWindow; 
        if (w.DataContext is TerminalViewModel vm) vm.Initialize(SelectedServer, command); 
        w.Show(); 
    }

    [RelayCommand]
    private async Task UpdateGeoDataAsync()
    {
        if (_currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;

        // ВНЕДРЕНО: Защита от дурака + try-catch обертка
        var result = System.Windows.MessageBox.Show(
            "Запустить скачивание свежих баз GeoIP и GeoSite с GitHub?\nЭто может занять несколько секунд.",
            "Обновление баз", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            ServerStatus = "Скачивание и обновление баз GeoSite...";
            var (success, msg) = await _xrayConfigurator.UpdateGeoDataAsync(_currentMonitoringSsh);

            if (success)
            {
                ServerStatus = "Онлайн (Базы GeoSite успешно обновлены!)";

                // Чтобы базы подхватились, нужно мягко перечитать конфиг
                if (SelectedServer.CoreType == "sing-box")
                    await _currentMonitoringSsh.ExecuteCommandAsync("killall -HUP sing-box 2>/dev/null");
            }
            else
            {
                ServerStatus = $"ОШИБКА: {msg}";
            }
        }
        catch (Exception ex)
        {
            ServerStatus = "КРАШ при обновлении баз";
            _logger.Log("GEO-ERROR", ex.Message);
        }
    }

    [RelayCommand]
    private void CopyXrayLogs()
    {
        if (string.IsNullOrWhiteSpace(XrayLogs)) return;

        try
        {
            // ВНЕДРЕНО: 100% защита от ThreadStateException. 
            // Буфер обмена доступен ТОЛЬКО из главного UI-потока (STA).
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Clipboard.SetText(XrayLogs);
            });

            ServerStatus = "Логи скопированы в буфер обмена!";
        }
        catch (Exception ex)
        {
            _logger.Log("UI-ERROR", $"Ошибка копирования: {ex.Message}");
            ServerStatus = "Ошибка копирования логов";
        }
    }

    [RelayCommand]
    private async Task RestartXrayAsync()
    {
        var ssh = _currentMonitoringSsh;
        var server = SelectedServer;
        if (ssh == null || !ssh.IsConnected || server == null) return;

        // ВНЕДРЕНО: Защита от дурака (предотвращение случайного клика)
        var result = System.Windows.MessageBox.Show(
            $"Вы уверены, что хотите жестко перезапустить ядро ({server.CoreType})?\nТекущие сессии пользователей будут кратковременно разорваны!",
            "Подтверждение рестарта", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        ServerStatus = $"Перезапуск {server.CoreType}...";
        try
        {
            string svc = server.CoreType.ToLower();
            string cmd = $"systemctl restart {svc}";

            // Умная логика: если TrustTunnel установлен параллельно, рестартуем и его
            if (server.Inbounds.Any(i => i.Protocol.ToLower() == "trusttunnel") && svc != "trusttunnel")
            {
                cmd += " && systemctl restart trusttunnel";
            }

            await ssh.ExecuteCommandAsync(cmd);
            ServerStatus = "Онлайн (Ядро перезапущено)";
        }
        catch (Exception ex)
        {
            ServerStatus = "Ошибка перезапуска ядра";
            _logger.Log("RESTART-ERROR", ex.Message);
        }
    }

    [RelayCommand]
    private async Task RebootServerAsync()
    {
        var ssh = _currentMonitoringSsh;
        if (ssh == null || !ssh.IsConnected) return;

        // ВНЕДРЕНО: Строгая защита от случайного ребута физического сервера
        var result = System.Windows.MessageBox.Show(
            "ВНИМАНИЕ! Это полностью перезагрузит физический сервер.\nСвязь с панелью будет потеряна на 1-3 минуты. Продолжить?",
            "Перезагрузка сервера", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Error);

        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            ServerStatus = "Отправка команды reboot...";
            _logger.Log("SYSTEM", $"Инициирована физическая перезагрузка сервера {SelectedServer?.IpAddress}");

            // ВНЕДРЕНО: Fire-and-forget выполнение.
            // Мы не делаем await для завершения команды, так как сервер умрет мгновенно
            // и SSH.NET выкинет SocketException из-за обрыва соединения.
            _ = ssh.ExecuteCommandAsync("reboot");

            // Ждем 1 секунду, чтобы пакет успел уйти в сокет
            await Task.Delay(1000);
        }
        catch
        {
            // Ожидаемый обрыв связи игнорируется
        }
        finally
        {
            ServerStatus = "Сервер ушел в перезагрузку (Offline)";
            StopMonitoring();
            ssh.Disconnect();
            if (_currentMonitoringSsh == ssh) _currentMonitoringSsh = null;
        }
    }
}
