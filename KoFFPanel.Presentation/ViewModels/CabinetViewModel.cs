using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace KoFFPanel.Presentation.ViewModels;

public partial class CabinetViewModel : ObservableObject
{
    private readonly IServerMonitorService _monitorService;
    private readonly IProfileRepository _profileRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly IXrayCoreService _xrayService;
    private readonly IXrayConfiguratorService _xrayConfigurator;
    private readonly IXrayUserManagerService _userManager;
    private readonly IDatabaseBackupService _backupService;
    private readonly ISubscriptionService _subscriptionService;

    private readonly Dictionary<string, long> _previousTrafficStats = new();
    private readonly Dictionary<string, HashSet<string>> _dailyIps = new();
    private readonly Dictionary<string, string> _lastKnownCountry = new();
    private readonly Dictionary<string, DateTime> _lastKnownCountryTime = new();
    private DateTime _currentDay = DateTime.Today;
    private readonly Func<ISshService> _sshServiceFactory;
    private CancellationTokenSource? _monitoringCts;
    private ISshService? _currentMonitoringSsh;

    [ObservableProperty] private string _title = "KoFFPanel - Управление серверами";
    [ObservableProperty] private int _serversCount = 0;
    [ObservableProperty] private ObservableCollection<VpnProfile> _servers = new();
    [ObservableProperty] private VpnProfile? _selectedServer;

    [ObservableProperty] private string _serverStatus = "Ожидание...";
    [ObservableProperty] private int _cpuUsage = 0;
    [ObservableProperty] private int _ramUsage = 0;
    [ObservableProperty] private int _ssdUsage = 0;
    [ObservableProperty] private long _pingMs = 0;
    [ObservableProperty] private bool _isMonitoringActive = false;
    [ObservableProperty] private string _uptime = "N/A";
    [ObservableProperty] private string _loadAverage = "0.00";
    [ObservableProperty] private string _networkSpeed = "0 Mbps";
    [ObservableProperty] private int _xrayProcesses = 0;
    [ObservableProperty] private int _tcpConnections = 0;
    [ObservableProperty] private int _synRecv = 0;
    [ObservableProperty] private int _errorRate = 0;

    [ObservableProperty] private int _totalUsers = 0;
    [ObservableProperty] private int _activeUsers = 0;
    [ObservableProperty] private string _totalTraffic = "0 B";

    [ObservableProperty] private string _xrayStatus = "Неизвестно";
    [ObservableProperty] private string _xrayLogs = "Ожидание логов...";
    [ObservableProperty] private string _xrayVersion = "Неизвестно";
    [ObservableProperty] private string _xrayConfigStatus = "Неизвестно";
    [ObservableProperty] private string _xrayUptime = "Остановлен";
    [ObservableProperty] private string _xrayMemory = "0.0 MB";
    [ObservableProperty] private string _xrayLastError = "Нет ошибок";

    [ObservableProperty] private ObservableCollection<VpnClient> _clients = new();

    public CabinetViewModel(
        IServerMonitorService monitorService, IProfileRepository profileRepository, IServiceProvider serviceProvider,
        IXrayCoreService xrayService, IXrayConfiguratorService xrayConfigurator, IXrayUserManagerService userManager,
        IDatabaseBackupService backupService, ISubscriptionService subscriptionService)
    {
        _monitorService = monitorService; _profileRepository = profileRepository; _serviceProvider = serviceProvider;
        _xrayService = xrayService; _xrayConfigurator = xrayConfigurator; _userManager = userManager;
        _backupService = backupService; _subscriptionService = subscriptionService;
        _sshServiceFactory = () => _serviceProvider.GetRequiredService<ISshService>();

        _ = _backupService.CreateBackupAsync();
        LoadData();
    }

    private string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0; decimal number = bytes;
        while (Math.Round(number / 1024) >= 1) { number /= 1024; counter++; }
        return string.Format("{0:n2} {1}", number, suffixes[counter]);
    }

    private void LoadData()
    {
        var profiles = _profileRepository.LoadProfiles();
        string? lastSelectedId = SelectedServer?.Id;
        Servers.Clear();
        if (profiles != null) foreach (var profile in profiles) Servers.Add(profile);
        ServersCount = Servers.Count;
        SelectedServer = (lastSelectedId != null && Servers.Any(s => s.Id == lastSelectedId)) ? Servers.First(s => s.Id == lastSelectedId) : Servers.FirstOrDefault();
    }

    partial void OnSelectedServerChanged(VpnProfile? value)
    {
        StopMonitoring();
        CpuUsage = 0; RamUsage = 0; SsdUsage = 0; PingMs = 0; Uptime = "N/A"; LoadAverage = "0.0"; NetworkSpeed = "0 Mbps";
        XrayProcesses = 0; TcpConnections = 0; SynRecv = 0; XrayStatus = "Ожидание..."; XrayLogs = "Ожидание логов...";
        Clients.Clear();
        if (value != null)
        {
            _monitoringCts = new CancellationTokenSource();
            _ = StartMonitoringLoopAsync(value, _monitoringCts.Token);
        }
    }

    private void StopMonitoring()
    {
        _monitoringCts?.Cancel(); _monitoringCts?.Dispose(); _monitoringCts = null;
    }

    [RelayCommand] private void AddServer() { var w = _serviceProvider.GetRequiredService<Views.AddServerWindow>(); w.ShowDialog(); LoadData(); }
    [RelayCommand] private void DeleteServer(VpnProfile? p) { if (p == null) return; _profileRepository.DeleteProfile(p.Id); if (SelectedServer?.Id == p.Id) SelectedServer = null; LoadData(); }
    [RelayCommand] private void EditServer(VpnProfile? p) { if (p == null) return; var w = _serviceProvider.GetRequiredService<Views.AddServerWindow>(); if (System.Windows.Application.Current.MainWindow != null) w.Owner = System.Windows.Application.Current.MainWindow; if (w.DataContext is AddServerViewModel vm) vm.LoadForEdit(p); w.ShowDialog(); LoadData(); }
    [RelayCommand] private void OpenDeployWizard() { if (SelectedServer == null) return; var w = _serviceProvider.GetRequiredService<Views.DeployWizardWindow>(); if (System.Windows.Application.Current.MainWindow != null) w.Owner = System.Windows.Application.Current.MainWindow; if (w.DataContext is DeployWizardViewModel vm) { vm.OnInstallRequested = OpenTerminalWithCommand; _ = vm.InitializeAsync(SelectedServer); } w.ShowDialog(); }
    [RelayCommand] private void OpenTerminal() { if (SelectedServer == null) return; OpenTerminalWithCommand(""); }
    private void OpenTerminalWithCommand(string command) { if (SelectedServer == null) return; var w = _serviceProvider.GetRequiredService<Views.TerminalWindow>(); if (System.Windows.Application.Current.MainWindow != null) w.Owner = System.Windows.Application.Current.MainWindow; if (w.DataContext is TerminalViewModel vm) vm.Initialize(SelectedServer, command); w.Show(); }
}