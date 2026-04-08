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
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.ViewModels;

public partial class CabinetViewModel : ObservableObject
{
    private readonly IServerMonitorService _monitorService;
    private readonly IProfileRepository _profileRepository;
    private readonly IServiceProvider _serviceProvider;
    private readonly IXrayCoreService _xrayService;
    private readonly IXrayConfiguratorService _xrayConfigurator;
    private readonly IXrayUserManagerService _userManager;
    private readonly Dictionary<string, long> _previousTrafficStats = new();
    private readonly Dictionary<string, HashSet<string>> _dailyIps = new();
    private readonly Dictionary<string, string> _lastKnownCountry = new();
    private readonly Dictionary<string, DateTime> _lastKnownCountryTime = new();
    private DateTime _currentDay = DateTime.Today;

    private readonly Func<ISshService> _sshServiceFactory;

    [ObservableProperty] private string _title = "KoFFPanel - Управление серверами";
    [ObservableProperty] private int _serversCount = 0;
    [ObservableProperty] private ObservableCollection<VpnProfile> _servers = new();
    [ObservableProperty] private string _xrayVersion = "Неизвестно";
    [ObservableProperty] private string _xrayConfigStatus = "Неизвестно";
    [ObservableProperty] private string _xrayUptime = "Остановлен";
    [ObservableProperty] private string _xrayMemory = "0.0 MB";
    [ObservableProperty] private string _xrayLastError = "Нет ошибок";

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

    [ObservableProperty] private int _totalUsers = 0;
    [ObservableProperty] private int _activeUsers = 0;
    [ObservableProperty] private string _totalTraffic = "0 B";

    [ObservableProperty] private string _xrayStatus = "Неизвестно";
    [ObservableProperty] private string _xrayLogs = "Ожидание логов...";
    [ObservableProperty] private ObservableCollection<VpnClient> _clients = new();
    [ObservableProperty] private string _vlessLink = "";
    [ObservableProperty] private bool _isLinkVisible = false;
    [ObservableProperty] private int _errorRate = 0;

    private CancellationTokenSource? _monitoringCts;
    private ISshService? _currentMonitoringSsh;

    public CabinetViewModel(
        IServerMonitorService monitorService,
        IProfileRepository profileRepository,
        IServiceProvider serviceProvider,
        IXrayCoreService xrayService,
        IXrayConfiguratorService xrayConfigurator,
        IXrayUserManagerService userManager)
    {
        _monitorService = monitorService;
        _profileRepository = profileRepository;
        _serviceProvider = serviceProvider;
        _xrayService = xrayService;
        _xrayConfigurator = xrayConfigurator;
        _userManager = userManager;

        _sshServiceFactory = () => _serviceProvider.GetRequiredService<ISshService>();

        LoadData();
    }

    [RelayCommand]
    private async Task GenerateRealityConfigAsync()
    {
        if (_currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;

        ServerStatus = "Сброс ядра и генерация VLESS...";

        var result = await _xrayConfigurator.InitializeRealityAsync(_currentMonitoringSsh, SelectedServer.IpAddress);

        if (result.IsSuccess)
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => {
                System.Windows.Clipboard.SetText(result.VlessLink);
            });

            // ПРИНУДИТЕЛЬНОЕ ОБНОВЛЕНИЕ ТАБЛИЦЫ
            // Заставляем панель мгновенно скачать свежий конфиг с сервера, 
            // чтобы Админ сразу появился на экране
            await LoadUsersAsync();

            var admin = Clients.FirstOrDefault(c => c.Email == "Админ");
            if (admin != null)
            {
                admin.VlessLink = result.VlessLink;
            }

            ServerStatus = "Онлайн (Сброс завершен, ссылка в буфере!)";
        }
        else
        {
            ServerStatus = $"ОШИБКА: {result.Message}";
        }
    }

    private string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;
        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }
        return string.Format("{0:n2} {1}", number, suffixes[counter]);
    }

    private void LoadData()
    {
        var profiles = _profileRepository.LoadProfiles();
        string? lastSelectedId = SelectedServer?.Id;

        if (Servers == null) Servers = new ObservableCollection<VpnProfile>();
        Servers.Clear();

        if (profiles != null)
        {
            foreach (var profile in profiles) Servers.Add(profile);
        }

        ServersCount = Servers.Count;

        if (lastSelectedId != null && Servers.Any(s => s.Id == lastSelectedId))
        {
            SelectedServer = Servers.First(s => s.Id == lastSelectedId);
        }
        else if (Servers.Any())
        {
            SelectedServer = Servers.First();
        }
    }

    partial void OnSelectedServerChanged(VpnProfile? value)
    {
        StopMonitoring();

        CpuUsage = 0; RamUsage = 0; SsdUsage = 0; PingMs = 0;
        Uptime = "N/A"; LoadAverage = "0.0"; NetworkSpeed = "0 Mbps"; XrayProcesses = 0;
        TcpConnections = 0; SynRecv = 0;
        XrayStatus = "Ожидание...";
        XrayLogs = "Ожидание логов...";

        if (Clients == null) Clients = new ObservableCollection<VpnClient>();
        Clients.Clear();

        if (value != null)
        {
            _monitoringCts = new CancellationTokenSource();
            _ = StartMonitoringLoopAsync(value, _monitoringCts.Token);
        }
    }

    private async Task StartMonitoringLoopAsync(VpnProfile profile, CancellationToken token)
    {
        IsMonitoringActive = true;
        ServerStatus = "Подключение...";

        ISshService localSsh = _sshServiceFactory();
        _currentMonitoringSsh = localSsh;

        string connResult = await localSsh.ConnectAsync(profile.IpAddress, profile.Port, profile.Username, profile.Password, profile.KeyPath);

        if (connResult != "SUCCESS")
        {
            ServerStatus = $"Ошибка: {connResult}";
            if (_currentMonitoringSsh == localSsh) IsMonitoringActive = false;
            localSsh.Disconnect();
            return;
        }

        ServerStatus = "Онлайн (Сбор данных)";
        await LoadUsersAsync();

        try
        {
            while (!token.IsCancellationRequested)
            {
                var pingResult = await _monitorService.PingServerAsync(profile.IpAddress);
                PingMs = pingResult.Success ? pingResult.RoundtripTime : 0;

                var resources = await _monitorService.GetResourcesAsync(localSsh);
                CpuUsage = resources.Cpu;
                RamUsage = resources.Ram;
                SsdUsage = resources.Ssd;
                Uptime = resources.Uptime;
                LoadAverage = resources.LoadAvg;
                NetworkSpeed = resources.NetworkSpeed;
                XrayProcesses = resources.XrayProcesses;
                TcpConnections = resources.TcpConnections;
                SynRecv = resources.SynRecv;
                ErrorRate = resources.ErrorRate;

                var onlineStats = await _monitorService.GetUserOnlineStatsAsync(localSsh);

                var coreStats = await _monitorService.GetCoreStatusInfoAsync(localSsh);

                string xrayStatusStr = await _xrayService.GetCoreStatusAsync(localSsh);

                string journalLogs = await _xrayService.GetCoreLogsAsync(localSsh, 5);
                string accessLogs = await localSsh.ExecuteCommandAsync("tail -n 5 /var/log/xray/access.log 2>/dev/null");
                string grepTest = await localSsh.ExecuteCommandAsync("tail -n 50 /var/log/xray/access.log 2>/dev/null | grep 'accepted' | tail -n 3");

                if (string.IsNullOrWhiteSpace(accessLogs)) accessLogs = "[Файл access.log пуст или не читается]";
                if (string.IsNullOrWhiteSpace(grepTest)) grepTest = "[Слово 'accepted' не найдено]";

                var trafficStats = await _userManager.GetTrafficStatsAsync(localSsh);

                System.Windows.Application.Current.Dispatcher.Invoke(() =>
                {
                    // ИСПРАВЛЕНИЕ: Жестко обновляем логи в потоке UI, чтобы WPF не терял их в скрытом аккордеоне
                    XrayStatus = xrayStatusStr;
                    XrayLogs = $"=== СИСТЕМНЫЙ ЖУРНАЛ ===\n{journalLogs.Trim()}\n\n" +
                               $"=== ФАЙЛ ACCESS.LOG ===\n{accessLogs.Trim()}\n\n" +
                               $"=== ТЕСТ ПАРСЕРА ===\n{grepTest.Trim()}";

                    XrayVersion = coreStats.Version;
                    XrayConfigStatus = coreStats.ConfigStatus;
                    XrayUptime = coreStats.Uptime;
                    XrayMemory = coreStats.MemoryUsage;
                    XrayLastError = coreStats.LastError;

                    long currentTotalBytes = 0;
                    bool dbNeedsUpdate = false;

                    if (DateTime.Today != _currentDay)
                    {
                        _dailyIps.Clear();
                        _currentDay = DateTime.Today;
                    }

                    foreach (var client in Clients)
                    {
                        long delta = 0;
                        if (trafficStats.TryGetValue(client.Email, out long currentXrayBytes))
                        {
                            long previousXrayBytes = _previousTrafficStats.TryGetValue(client.Email, out long prev) ? prev : 0;
                            delta = currentXrayBytes >= previousXrayBytes ? currentXrayBytes - previousXrayBytes : currentXrayBytes;
                            if (delta > 0)
                            {
                                client.TrafficUsed += delta;
                                dbNeedsUpdate = true;
                            }
                            _previousTrafficStats[client.Email] = currentXrayBytes;
                        }

                        var userLog = onlineStats.FirstOrDefault(s => s.Email == client.Email);
                        if (userLog != null)
                        {
                            client.LastIp = userLog.LastIp;
                            client.ActiveConnections = userLog.ActiveSessions;
                            client.LastOnline = DateTime.Now;

                            if (!string.IsNullOrEmpty(userLog.Country))
                            {
                                client.Country = userLog.Country;
                            }

                            if (client.IsAntiFraudEnabled)
                            {
                                string antiFraudReason = "";

                                if (!_dailyIps.ContainsKey(client.Email)) _dailyIps[client.Email] = new HashSet<string>();
                                _dailyIps[client.Email].Add(userLog.LastIp);

                                bool geoJumpDetected = false;
                                string currentCountryCode = userLog.Country.Length >= 2 ? userLog.Country.Substring(userLog.Country.Length - 2) : "";

                                if (!string.IsNullOrEmpty(currentCountryCode) && currentCountryCode != "??")
                                {
                                    if (_lastKnownCountry.TryGetValue(client.Email, out string lastCountry))
                                    {
                                        if (lastCountry != currentCountryCode)
                                        {
                                            if (_lastKnownCountryTime.TryGetValue(client.Email, out DateTime lastTime))
                                            {
                                                if ((DateTime.Now - lastTime).TotalHours < 2)
                                                {
                                                    geoJumpDetected = true;
                                                }
                                            }
                                        }
                                    }
                                    if (!geoJumpDetected)
                                    {
                                        _lastKnownCountry[client.Email] = currentCountryCode;
                                        _lastKnownCountryTime[client.Email] = DateTime.Now;
                                    }
                                }

                                if (client.ActiveConnections > 2)
                                    antiFraudReason = "ФРОД: >2 Устройств";
                                else if (_dailyIps[client.Email].Count > 5)
                                    antiFraudReason = "ФРОД: >5 IP за сутки";
                                else if (geoJumpDetected)
                                    antiFraudReason = "ФРОД: Резкая смена страны";
                                else if (delta > 1073741824L)
                                    antiFraudReason = "ФРОД: Скачок трафика";

                                if (!string.IsNullOrEmpty(antiFraudReason) && client.IsActive)
                                {
                                    client.IsActive = false;
                                    client.Note = antiFraudReason;
                                    dbNeedsUpdate = true;
                                    _ = BlockUserAsync(client, antiFraudReason);
                                }
                            }
                        }
                        else
                        {
                            client.ActiveConnections = 0;
                        }

                        currentTotalBytes += client.TrafficUsed;

                        bool isTrafficExceeded = client.TrafficLimit > 0 && client.TrafficUsed >= client.TrafficLimit;
                        bool isExpired = client.ExpiryDate.HasValue && client.ExpiryDate.Value.Date <= DateTime.Now.Date;

                        if ((isTrafficExceeded || isExpired) && client.IsActive)
                        {
                            client.IsActive = false;
                            _ = BlockUserAsync(client, isTrafficExceeded ? "Превышен лимит" : "Истек срок");
                        }
                    }

                    if (dbNeedsUpdate && SelectedServer != null)
                    {
                        _ = _userManager.SaveTrafficToDbAsync(SelectedServer.IpAddress, Clients);
                    }

                    TotalUsers = Clients.Count;
                    ActiveUsers = Clients.Count(c => c.IsActive);
                    TotalTraffic = FormatBytes(currentTotalBytes);
                });

                await Task.Delay(5000, token);
            }
        }
        catch (TaskCanceledException) { }
        catch (Exception) { ServerStatus = "Связь потеряна"; }
        finally
        {
            localSsh.Disconnect();
            if (_currentMonitoringSsh == localSsh)
            {
                _currentMonitoringSsh = null;
                IsMonitoringActive = false;
            }
        }
    }

    private void StopMonitoring()
    {
        _monitoringCts?.Cancel();
        _monitoringCts?.Dispose();
        _monitoringCts = null;
    }

    [RelayCommand]
    private void AddServer()
    {
        var addServerWindow = _serviceProvider.GetRequiredService<Views.AddServerWindow>();
        addServerWindow.ShowDialog();
        LoadData();
    }

    [RelayCommand]
    private void DeleteServer(VpnProfile? profile)
    {
        if (profile == null) return;
        _profileRepository.DeleteProfile(profile.Id);
        if (SelectedServer?.Id == profile.Id) SelectedServer = null;
        LoadData();
    }

    [RelayCommand]
    private void OpenDeployWizard()
    {
        if (SelectedServer == null) return;
        var deployWindow = _serviceProvider.GetRequiredService<Views.DeployWizardWindow>();

        if (System.Windows.Application.Current.MainWindow != null)
        {
            deployWindow.Owner = System.Windows.Application.Current.MainWindow;
        }

        if (deployWindow.DataContext is DeployWizardViewModel wizardVm)
        {
            wizardVm.OnInstallRequested = (command) =>
            {
                OpenTerminalWithCommand(command);
            };
            _ = wizardVm.InitializeAsync(SelectedServer);
        }

        deployWindow.ShowDialog();
    }

    private void OpenTerminalWithCommand(string command)
    {
        if (SelectedServer == null) return;
        var terminalWindow = _serviceProvider.GetRequiredService<Views.TerminalWindow>();

        if (System.Windows.Application.Current.MainWindow != null)
        {
            terminalWindow.Owner = System.Windows.Application.Current.MainWindow;
        }

        if (terminalWindow.DataContext is TerminalViewModel terminalVm)
        {
            terminalVm.Initialize(SelectedServer, command);
        }
        terminalWindow.Show();
    }

    [RelayCommand]
    private void OpenTerminal()
    {
        if (SelectedServer == null)
        {
            ServerStatus = "ОШИБКА: Сначала выберите сервер!";
            return;
        }

        var terminalWindow = _serviceProvider.GetRequiredService<Views.TerminalWindow>();

        if (System.Windows.Application.Current.MainWindow != null)
        {
            terminalWindow.Owner = System.Windows.Application.Current.MainWindow;
        }

        if (terminalWindow.DataContext is TerminalViewModel vm)
        {
            vm.Initialize(SelectedServer, "");
        }
        terminalWindow.Show();
    }

    [RelayCommand]
    private async Task RestartXrayAsync()
    {
        if (_currentMonitoringSsh != null && _currentMonitoringSsh.IsConnected)
        {
            await _xrayService.RestartCoreAsync(_currentMonitoringSsh);
            XrayLogs += "\n[Система]: Команда на рестарт отправлена...";
        }
    }

    private async Task LoadUsersAsync()
    {
        if (_currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;
        var realUsers = await _userManager.GetUsersAsync(_currentMonitoringSsh, SelectedServer.IpAddress);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Clients.Clear();
            foreach (var u in realUsers) Clients.Add(u);
        });
    }

    [RelayCommand]
    private async Task AddClientAsync()
    {
        if (_currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;

        var addClientWindow = _serviceProvider.GetRequiredService<Views.AddClientWindow>();
        if (System.Windows.Application.Current.MainWindow != null)
        {
            addClientWindow.Owner = System.Windows.Application.Current.MainWindow;
        }

        addClientWindow.ShowDialog();

        if (addClientWindow.DataContext is AddClientViewModel vm && vm.IsSuccess)
        {
            ServerStatus = $"Создание клиента {vm.ClientName}...";
            long limitBytes = (long)vm.TrafficLimitGb * 1024 * 1024 * 1024;
            var (success, msg) = await _userManager.AddUserAsync(_currentMonitoringSsh, SelectedServer.IpAddress, vm.ClientName, limitBytes, vm.ExpiryDate);

            if (success)
            {
                ServerStatus = $"Онлайн (Клиент {vm.ClientName} добавлен!)";
                await LoadUsersAsync();
            }
            else
            {
                ServerStatus = $"ОШИБКА: {msg}";
            }
        }
    }

    [RelayCommand]
    private async Task EditClientAsync(VpnClient? client)
    {
        if (client == null || _currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;

        var addClientWindow = _serviceProvider.GetRequiredService<Views.AddClientWindow>();
        if (System.Windows.Application.Current.MainWindow != null)
        {
            addClientWindow.Owner = System.Windows.Application.Current.MainWindow;
        }

        if (addClientWindow.DataContext is AddClientViewModel vm)
        {
            vm.LoadForEdit(client.Email, client.TrafficLimit, client.ExpiryDate, client.Note);
        }

        addClientWindow.ShowDialog();

        if (addClientWindow.DataContext is AddClientViewModel resultVm && resultVm.IsSuccess)
        {
            ServerStatus = $"Обновление лимитов для {client.Email}...";
            long newLimitBytes = (long)resultVm.TrafficLimitGb * 1024 * 1024 * 1024;
            bool success = await _userManager.UpdateUserLimitsAsync(SelectedServer.IpAddress, client.Email, newLimitBytes, resultVm.ExpiryDate);

            if (success)
            {
                client.TrafficLimit = newLimitBytes;
                client.ExpiryDate = resultVm.ExpiryDate;
                client.Note = resultVm.Note;
                ServerStatus = $"Онлайн (Лимиты {client.Email} обновлены)";
            }
            else
            {
                ServerStatus = "ОШИБКА: Не удалось обновить лимиты";
            }
        }
    }

    [RelayCommand]
    private async Task DeleteClientAsync(VpnClient? client)
    {
        if (client == null || _currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;

        ServerStatus = $"Удаление клиента {client.Email}...";

        var (success, msg) = await _userManager.RemoveUserAsync(_currentMonitoringSsh, SelectedServer.IpAddress, client.Email);

        System.Windows.Application.Current.Dispatcher.Invoke(() => Clients.Remove(client));

        if (success)
        {
            ServerStatus = $"Онлайн (Клиент {client.Email} удален)";
        }
        else
        {
            ServerStatus = $"Принудительная очистка. (Ядро ответило: {msg})";
        }
    }

    [RelayCommand]
    private void EditServer(VpnProfile? profile)
    {
        if (profile == null) return;

        var editWindow = _serviceProvider.GetRequiredService<Views.AddServerWindow>();

        if (System.Windows.Application.Current.MainWindow != null)
        {
            editWindow.Owner = System.Windows.Application.Current.MainWindow;
        }

        if (editWindow.DataContext is AddServerViewModel vm)
        {
            vm.LoadForEdit(profile);
        }

        editWindow.ShowDialog();

        bool wasSelected = (SelectedServer?.Id == profile.Id);
        if (wasSelected) SelectedServer = null;

        LoadData();
    }

    [RelayCommand]
    private void CopyClientLink(VpnClient? client)
    {
        if (client != null && !string.IsNullOrEmpty(client.VlessLink))
        {
            System.Windows.Clipboard.SetText(client.VlessLink);
            ServerStatus = $"Ссылка для {client.Email} скопирована в буфер!";
        }
    }

    [RelayCommand]
    private async Task ResetClientTrafficAsync(VpnClient? client)
    {
        if (client == null || _currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;

        ServerStatus = $"Сброс трафика для {client.Email}...";
        bool success = await _userManager.ResetTrafficAsync(_currentMonitoringSsh, client.Email);

        if (success)
        {
            client.TrafficUsed = 0;
            _previousTrafficStats[client.Email] = 0;
            await _userManager.SaveTrafficToDbAsync(SelectedServer.IpAddress, new[] { client });
            ServerStatus = $"Онлайн (Трафик для {client.Email} обнулен)";
        }
        else
        {
            ServerStatus = "ОШИБКА: Не удалось сбросить трафик";
        }
    }

    private async Task BlockUserAsync(VpnClient client, string reason)
    {
        if (_currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;

        ServerStatus = $"Блокировка {client.Email} ({reason})...";
        var (success, msg) = await _userManager.ToggleUserStatusAsync(_currentMonitoringSsh, SelectedServer.IpAddress, client.Email, false);

        if (success)
            ServerStatus = $"Онлайн ({client.Email} заблокирован)";
        else
            ServerStatus = $"Ошибка блокировки: {msg}";
    }

    [RelayCommand]
    private async Task ToggleClientAccessAsync(VpnClient? client)
    {
        if (client == null || _currentMonitoringSsh == null || !_currentMonitoringSsh.IsConnected || SelectedServer == null) return;

        bool newState = !client.IsActive;
        string actionName = newState ? "Разблокировка" : "Блокировка";

        ServerStatus = $"{actionName} {client.Email}...";
        var (success, msg) = await _userManager.ToggleUserStatusAsync(_currentMonitoringSsh, SelectedServer.IpAddress, client.Email, newState);

        if (success)
        {
            client.IsActive = newState;

            // Если мы разблокируем пользователя, очищаем причину бана в заметках
            if (newState && (client.Note?.StartsWith("ФРОД:") == true || client.Note == "Превышен лимит" || client.Note == "Истек срок"))
            {
                client.Note = "";
            }

            // Сбрасываем счетчик IP для этого пользователя, чтобы система не забанила его повторно мгновенно
            if (newState && _dailyIps.ContainsKey(client.Email))
            {
                _dailyIps[client.Email].Clear();
            }

            ServerStatus = $"Онлайн ({client.Email} {(newState ? "разблокирован" : "заблокирован")})";
        }
        else
        {
            ServerStatus = $"ОШИБКА: {msg}";
        }
    }

    [RelayCommand]
    private void CopyXrayLogs()
    {
        if (!string.IsNullOrEmpty(XrayLogs))
        {
            System.Windows.Clipboard.SetText(XrayLogs);
            ServerStatus = "Логи ядра скопированы в буфер!";
        }
    }
}