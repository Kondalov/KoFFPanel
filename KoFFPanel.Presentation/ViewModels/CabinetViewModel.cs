using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Messaging;
using KoFFPanel.Presentation.Messages;

namespace KoFFPanel.Presentation.ViewModels;

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

    // ДОБАВЛЕНО 1: Объявляем переменную для логгера
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

    // ДОБАВЛЕНО 2: Добавляем IAppLogger logger в параметры конструктора
    public CabinetViewModel(
        IServerMonitorService monitorService, IProfileRepository profileRepository, IServiceProvider serviceProvider,
        IXrayCoreService xrayService, IXrayConfiguratorService xrayConfigurator, IXrayUserManagerService userManager,
        IDatabaseBackupService backupService, ISubscriptionService subscriptionService, IClientAnalyticsService analyticsService,
        ISingBoxUserManagerService singBoxUserManager, IAppLogger logger)
    {
        _monitorService = monitorService; _profileRepository = profileRepository; _serviceProvider = serviceProvider;
        _xrayService = xrayService; _xrayConfigurator = xrayConfigurator; _userManager = userManager; _singBoxUserManager = singBoxUserManager;
        _backupService = backupService; _subscriptionService = subscriptionService;

        // ДОБАВЛЕНО 3: Сохраняем переданный логгер в нашу переменную
        _logger = logger;

        _sshServiceFactory = () => _serviceProvider.GetRequiredService<ISshService>();

        _ = _backupService.CreateBackupAsync();
        _analyticsService = analyticsService;

        LoadAvatarRegistry();
        Clients.CollectionChanged += Clients_CollectionChanged;
        WeakReferenceMessenger.Default.Register(this);

        LoadData();
        _singBoxUserManager = singBoxUserManager;
    }

    // ========================================================================
    // РЕАЛИЗАЦИЯ ИНТЕРФЕЙСА IRecipient<CoreDeployedMessage>
    // ========================================================================
    public async void Receive(CoreDeployedMessage message)
    {
        if (SelectedServer == null || message.Server == null || message.Server.Id != SelectedServer.Id)
            return;

        var ssh = _currentMonitoringSsh;
        if (ssh == null || !ssh.IsConnected) return;

        SelectedServer.PublicKey = message.Server.PublicKey ?? "";
        SelectedServer.PrivateKey = message.Server.PrivateKey ?? "";
        SelectedServer.ShortId = message.Server.ShortId ?? "";
        SelectedServer.Sni = message.Server.Sni ?? "www.microsoft.com";
        SelectedServer.VpnPort = message.Server.VpnPort;

        System.Windows.Application.Current.Dispatcher.Invoke(() => ServerStatus = "Синхронизация БД с ядром...");

        // Определяем, какое ядро сейчас работает
        string activeCoreCmd = "systemctl is-active --quiet sing-box && echo 'Sing-box' || echo 'Xray-core'";
        string activeCoreName = (await ssh.ExecuteCommandAsync(activeCoreCmd)).Trim();
        bool isSingBox = activeCoreName.Equals("Sing-box", StringComparison.OrdinalIgnoreCase);

        string ip = SelectedServer.IpAddress ?? "";
        string pubKey = SelectedServer.PublicKey ?? "";
        string sni = SelectedServer.Sni ?? "www.microsoft.com";
        string shortId = SelectedServer.ShortId ?? "";

        // 1. Физическая синхронизация юзеров с JSON конфигом ядра
        bool syncSuccess = isSingBox
            ? await _singBoxUserManager.SyncUsersToCoreAsync(ssh, Clients)
            : await _userManager.SyncUsersToCoreAsync(ssh, Clients);

        // 2. Обновление ссылок в UI и локальной БД
        foreach (var client in Clients)
        {
            string uuid = client.Uuid ?? "";
            // Ссылка формируется по универсальному VLESS Reality стандарту Hiddify 4.1.1+
            string newVless = $"vless://{uuid}@{ip}:{SelectedServer.VpnPort}?type=tcp&security=reality&pbk={pubKey}&fp=chrome&sni={sni}&sid={shortId}&spx=%2F&flow=xtls-rprx-vision#KoFFPanel-{client.Email}";

            client.VlessLink = newVless;

            if (syncSuccess)
            {
                // Записываем новую ссылку в базу данных, чтобы она не пропала после рестарта панели
                await _subscriptionService.UpdateUserSubscriptionAsync(ssh, uuid, newVless);
            }
        }

        // Обновляем статус в главном потоке
        System.Windows.Application.Current.Dispatcher.Invoke(() => {
            ServerStatus = syncSuccess
                ? $"Онлайн (Все {Clients.Count} клиентов синхронизированы с {activeCoreName})"
                : "Ошибка синхронизации БД с ядром.";
        });
    }

    private void LoadAvatarRegistry()
    {
        try
        {
            if (File.Exists(_avatarsRegistryPath))
            {
                string json = File.ReadAllText(_avatarsRegistryPath);
                _avatarRegistry = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
            }
        }
        catch { }
    }

    private void SaveAvatarRegistry()
    {
        try
        {
            string dir = Path.GetDirectoryName(_avatarsRegistryPath)!;
            Directory.CreateDirectory(dir);
            string json = JsonSerializer.Serialize(_avatarRegistry);
            File.WriteAllText(_avatarsRegistryPath, json);
        }
        catch { }
    }

    // ИСПРАВЛЕНИЕ: Умный наблюдатель. Подписывается на КАЖДОГО клиента в таблице
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
                // Слушаем изменения внутри этого клиента
                c.PropertyChanged += Client_PropertyChanged;
            }
        }

        if (e.OldItems != null)
        {
            foreach (VpnClient c in e.OldItems)
            {
                // Отписываемся при удалении
                c.PropertyChanged -= Client_PropertyChanged;
            }
        }

        RecalculateActiveUsers();
    }

    // ИСПРАВЛЕНИЕ: Мгновенно реагируем, если у кого-то поменялся онлайн
    private void Client_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VpnClient.ActiveConnections))
        {
            RecalculateActiveUsers();
        }
    }

    // ИСПРАВЛЕНИЕ: Считаем реальных онлайн клиентов (у кого подключений > 0)
    private void RecalculateActiveUsers()
    {
        TotalUsers = Clients.Count;
        ActiveUsers = Clients.Count(c => c.ActiveConnections > 0);
    }

    [RelayCommand]
    private void NavigateToDashboard()
    {
        ActiveMenu = "Dashboard";
        var view = _serviceProvider.GetRequiredService<Views.Pages.DashboardView>();
        view.DataContext = this;
        CurrentView = view;
    }

    [RelayCommand]
    private void NavigateToClients()
    {
        ActiveMenu = "Clients";
        var view = _serviceProvider.GetRequiredService<Views.Pages.ClientsView>();
        view.DataContext = this;
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
            NavigateToDashboard();
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

    [RelayCommand]
    private async Task ChangeAvatarAsync(VpnClient? client)
    {
        if (client == null) return;

        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите аватар (Изображение будет автоматически сжато)",
            Filter = "Изображения|*.jpg;*.jpeg;*.png;*.bmp|Все файлы|*.*"
        };

        if (openFileDialog.ShowDialog() != true) return;

        try
        {
            string sourcePath = openFileDialog.FileName;
            string avatarsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Avatars");
            Directory.CreateDirectory(avatarsFolder);

            string destFileName = $"avatar_{client.Email}_{Guid.NewGuid().ToString("N").Substring(0, 8)}.jpg";
            string destPath = Path.Combine(avatarsFolder, destFileName);

            await Task.Run(() =>
            {
                var originalImage = new BitmapImage();
                originalImage.BeginInit();
                originalImage.UriSource = new Uri(sourcePath);
                originalImage.CacheOption = BitmapCacheOption.OnLoad;
                originalImage.EndInit();
                originalImage.Freeze();

                int size = Math.Min(originalImage.PixelWidth, originalImage.PixelHeight);
                int x = (originalImage.PixelWidth - size) / 2;
                int y = (originalImage.PixelHeight - size) / 2;

                var croppedBitmap = new CroppedBitmap(originalImage, new System.Windows.Int32Rect(x, y, size, size));
                var scaledBitmap = new TransformedBitmap(croppedBitmap, new ScaleTransform(64.0 / size, 64.0 / size));

                var encoder = new JpegBitmapEncoder { QualityLevel = 85 };
                encoder.Frames.Add(BitmapFrame.Create(scaledBitmap));

                using var fileStream = new FileStream(destPath, FileMode.Create);
                encoder.Save(fileStream);
            });

            client.AvatarPath = destPath;

            if (!string.IsNullOrEmpty(client.Email))
            {
                _avatarRegistry[client.Email] = destPath;
                SaveAvatarRegistry();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ошибка при сжатии аватара: {ex.Message}");
        }
    }
}