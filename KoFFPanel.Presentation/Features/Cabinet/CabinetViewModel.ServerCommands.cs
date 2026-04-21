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

        ServerStatus = "Скачивание и обновление баз GeoSite...";
        var (success, msg) = await _xrayConfigurator.UpdateGeoDataAsync(_currentMonitoringSsh);

        if (success)
            ServerStatus = "Онлайн (Базы GeoSite успешно обновлены!)";
        else
            ServerStatus = $"ОШИБКА: {msg}";
    }
}
