using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Presentation.Messages;
using KoFFPanel.Presentation.Features.Bot;
using KoFFPanel.Presentation.Features.Cabinet;
using KoFFPanel.Presentation.Features.Terminal;
using KoFFPanel.Presentation.Features.Deploy;
using KoFFPanel.Presentation.Features.Analytics;
using KoFFPanel.Presentation.Features.Management;
using KoFFPanel.Presentation.Features.Config;
using KoFFPanel.Presentation.Features.Shared.Dialogs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.Features.Cabinet;

public partial class CabinetViewModel : ObservableObject, IRecipient<CoreDeployedMessage>
{
    private readonly IServerMonitorService _monitorService;
    private readonly IProfileRepository _profileRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly IXrayCoreService _xrayService;
    private readonly IXrayConfiguratorService _xrayConfigurator;
    private readonly IXrayUserManagerService _userManager;
    private readonly IDatabaseBackupService _backupService;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IClientAnalyticsService _analyticsService;
    private readonly ISingBoxUserManagerService _singBoxUserManager;
    private readonly ITrustTunnelUserManagerService _trustTunnelUserManager;
    private readonly IAppLogger _logger;

    private readonly Dictionary<string, long> _previousTrafficStats = new();
    private readonly Dictionary<string, HashSet<string>> _dailyIps = new();
    private readonly Dictionary<string, string> _lastKnownCountry = new();
    private readonly Dictionary<string, DateTime> _lastKnownCountryTime = new();
    private DateTime _currentDay = DateTime.Today;
    private readonly Func<ISshService> _sshServiceFactory;
    private CancellationTokenSource? _monitoringCts;
    private ISshService? _currentMonitoringSsh;

    private readonly string _avatarsRegistryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Avatars", "registry.json");
    private Dictionary<string, string> _avatarRegistry = new();

    [ObservableProperty] private object? _currentView;
    [ObservableProperty] private string _activeMenu = "Dashboard";

    [ObservableProperty] private string _title = "KoFFPanel - Управление серверами";
    [ObservableProperty] private int _serversCount = 0;
    [ObservableProperty] private ObservableCollection<VpnProfile> _servers = new();
    [ObservableProperty] private VpnProfile? _selectedServer;
    [ObservableProperty] private string _activeCoreTitle = "Ядро (Ожидание)";

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
        IDatabaseBackupService backupService, ISubscriptionService subscriptionService, IClientAnalyticsService analyticsService,
        ISingBoxUserManagerService singBoxUserManager, ITrustTunnelUserManagerService trustTunnelUserManager, IAppLogger logger)
    {
        _monitorService = monitorService; _profileRepository = profileRepository; _serviceProvider = serviceProvider;
        _xrayService = xrayService; _xrayConfigurator = xrayConfigurator; _userManager = userManager; _singBoxUserManager = singBoxUserManager;
        _trustTunnelUserManager = trustTunnelUserManager; _backupService = backupService; _subscriptionService = subscriptionService;
        _logger = logger;

        _sshServiceFactory = () => _serviceProvider.GetRequiredService<ISshService>();
        _ = _backupService.CreateBackupAsync();
        _analyticsService = analyticsService;

        LoadAvatarRegistry();
        Clients.CollectionChanged += Clients_CollectionChanged;
        WeakReferenceMessenger.Default.Register(this);

        LoadData();
    }

    private void Clients_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
        {
            foreach (VpnClient c in e.NewItems)
            {
                if (!string.IsNullOrEmpty(c.Email) && _avatarRegistry.TryGetValue(c.Email, out string path))
                {
                    if (File.Exists(path))
                    {
                        c.AvatarPath = path;
                    }
                }
                c.PropertyChanged += Client_PropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (VpnClient c in e.OldItems)
            {
                c.PropertyChanged -= Client_PropertyChanged;
            }
        }

        RecalculateActiveUsers();
    }

    private void Client_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VpnClient.ActiveConnections))
        {
            RecalculateActiveUsers();
        }
    }

    private void RecalculateActiveUsers()
    {
        TotalUsers = Clients.Count;
        ActiveUsers = Clients.Count(c => c.ActiveConnections > 0);
    }

    [RelayCommand]
    private void NavigateToDashboard()
    {
        ActiveMenu = "Dashboard";
        var view = _serviceProvider.GetRequiredService<DashboardView>();
        view.DataContext = this;
        CurrentView = view;
    }

    [RelayCommand]
    private void NavigateToClients()
    {
        ActiveMenu = "Clients";
        var view = _serviceProvider.GetRequiredService<ClientsView>();
        view.DataContext = this;
        CurrentView = view;
    }

    [RelayCommand]
    private void NavigateToBot()
    {
        ActiveMenu = "Bot";
        var view = _serviceProvider.GetRequiredService<BotView>();
        view.DataContext = _serviceProvider.GetRequiredService<BotViewModel>();
        CurrentView = view;
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

        if (profiles != null)
        {
            foreach (var profile in profiles)
            {
                profile.MigrateLegacyData();
                Servers.Add(profile);
            }
        }

        ServersCount = Servers.Count;
        SelectedServer = (lastSelectedId != null && Servers.Any(s => s.Id == lastSelectedId)) ? Servers.First(s => s.Id == lastSelectedId) : Servers.FirstOrDefault();
    }

    private string? _lastConnectionKey;

    partial void OnSelectedServerChanged(VpnProfile? value)
    {
        string? currentConnectionKey = value == null ? null : $"{value.Id}|{value.IpAddress}|{value.Port}|{value.Username}|{value.Password}|{value.KeyPath}";

        if (currentConnectionKey == _lastConnectionKey)
        {
            // Если данные подключения не изменились, не перезапускаем цикл мониторинга
            _logger.Log("CABINET", "Выбран тот же сервер, пропуск перезапуска мониторинга.");
            return;
        }

        _lastConnectionKey = currentConnectionKey;
        StopMonitoring();
        CpuUsage = 0; RamUsage = 0; SsdUsage = 0; PingMs = 0; Uptime = "N/A"; LoadAverage = "0.0"; NetworkSpeed = "0 Mbps";
        XrayProcesses = 0; TcpConnections = 0; SynRecv = 0; XrayStatus = "Ожидание..."; XrayLogs = "Ожидание логов...";

        Clients.Clear();

        if (value != null)
        {
            ActiveCoreTitle = value.CoreType == "sing-box" ? "Ядро (Sing-box)" : (value.CoreType == "trusttunnel" ? "Ядро (TrustTunnel)" : "Ядро (Xray-core)");

            try
            {
                string currentIp = value.IpAddress ?? "";
                var dbContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();

                var dbUsers = dbContext.Clients.Where(c => c.ServerIp == currentIp).ToList();
                foreach (var u in dbUsers) Clients.Add(u);

                _logger.Log("DB-LOAD", $"Успешно загружено {Clients.Count} клиентов для сервера {currentIp} из базы данных SQLite.");
            }
            catch (Exception ex)
            {
                _logger.Log("DB-ERROR", $"Ошибка чтения БД SQLite: {ex.Message}");
            }

            NavigateToDashboard();
            _monitoringCts = new CancellationTokenSource();
            _ = StartMonitoringLoopAsync(value, _monitoringCts.Token);
        }
    }

    private void StopMonitoring()
    {
        _monitoringCts?.Cancel(); _monitoringCts?.Dispose(); _monitoringCts = null;
    }
}
