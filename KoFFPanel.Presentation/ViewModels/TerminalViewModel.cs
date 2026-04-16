using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.ViewModels;

public partial class TerminalViewModel : ObservableObject, IDisposable
{
    private readonly ISshService _sshService;
    private readonly IAppLogger _logger;
    private readonly IServerMonitorService _monitorService;
    private System.Timers.Timer? _monitoringTimer;
    public event Action<string, string>? OnFileReadyForEdit;
    private string GetSnippetsFilePath() => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Snippets", "snippets.json");
    public IAppLogger Logger => _logger;
    
    private WebView2? _webView;
    private Renci.SshNet.ShellStream? _shellStream;
    private CancellationTokenSource? _readCts;

    private bool _isWebViewReady = false;
    private StringBuilder _outputBuffer = new StringBuilder();

    [ObservableProperty] private string _windowTitle = "SSH Терминал";
    [ObservableProperty] private string _connectionInfo = "Ожидание...";
    [ObservableProperty] private bool _isConnected = false;
    [ObservableProperty] private long _pingMs = 0;

    [ObservableProperty] private string _currentDirectory = "/root";
    [ObservableProperty] private ObservableCollection<RemoteFileItem> _files = new();
    [ObservableProperty] private int _cpuUsage = 0;
    [ObservableProperty] private int _ramUsage = 0;
    [ObservableProperty] private int _ssdUsage = 0;
    [ObservableProperty] private string _commandInput = "";
    [ObservableProperty] private ObservableCollection<SnippetCategory> _snippetCategories = new();
    [ObservableProperty] private SnippetCategory? _selectedSnippetCategory;
    [ObservableProperty] private SnippetSubCategory? _selectedSnippetSubCategory;

    partial void OnSelectedSnippetCategoryChanged(SnippetCategory? value)
    {
        if (value != null && value.SubCategories.Any())
            SelectedSnippetSubCategory = value.SubCategories.First();
        else
            SelectedSnippetSubCategory = null;
    }
    public VpnProfile? CurrentProfile { get; private set; }

    public TerminalViewModel(ISshService sshService, IAppLogger logger, IServerMonitorService monitorService)
    {
        _sshService = sshService;
        _logger = logger;
        _monitorService = monitorService;
    }

    public void Initialize(VpnProfile profile, string command)
    {
        CurrentProfile = profile;
        WindowTitle = $"Терминал — {profile.Name} ({profile.IpAddress})";
        ConnectionInfo = $"Подключение к {profile.Username}@{profile.IpAddress}...";
        _logger.Log("TERM-INIT", $"Инициализация профиля: {profile.IpAddress}");

        LoadSnippets();
    }

    public void LoadSnippets()
    {
        try
        {
            string filePath = GetSnippetsFilePath();

            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                var data = System.Text.Json.JsonSerializer.Deserialize<ObservableCollection<SnippetCategory>>(json);
                if (data != null)
                {
                    SnippetCategories = data;
                }
                _logger.Log("TERM-SNIP", "Сниппеты успешно загружены из папки Snippets.");
            }
            else
            {
                // Если файла еще нет, создаем структуру по умолчанию
                var cat = new SnippetCategory { Name = "Server" };
                var sub = new SnippetSubCategory { Name = "Общие" };
                sub.Snippets.Add(new SnippetItem { Description = "Обновление системы", Command = "apt update && apt upgrade -y" });
                cat.SubCategories.Add(sub);
                SnippetCategories.Add(cat);

                // Сразу сохраняем, чтобы создалась папка и файл
                SaveSnippets();
            }

            if (SnippetCategories.Any()) SelectedSnippetCategory = SnippetCategories.First();
        }
        catch (Exception ex)
        {
            _logger.Log("TERM-SNIP-ERR", $"Ошибка загрузки: {ex.Message}");
        }
    }

    public void SaveSnippets()
    {
        try
        {
            string filePath = GetSnippetsFilePath();
            string? directory = Path.GetDirectoryName(filePath);

            // Проверяем наличие папки Snippets и создаем её, если нужно
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            string json = System.Text.Json.JsonSerializer.Serialize(SnippetCategories, options);

            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.Log("TERM-SNIP-ERR", $"Ошибка сохранения: {ex.Message}");
        }
    }

    // === КОМАНДЫ ДЛЯ XAML ===
    [RelayCommand]
    private void InsertSnippet(SnippetItem item)
    {
        if (item != null) CommandInput = item.Command; // Вставляет команду в нижнее окно ввода
    }

    [RelayCommand]
    private void DeleteSnippet(SnippetItem item)
    {
        if (SelectedSnippetSubCategory != null && item != null)
        {
            SelectedSnippetSubCategory.Snippets.Remove(item);
            SaveSnippets();
        }
    }

    [RelayCommand]
    private void DeleteCategory(SnippetCategory cat)
    {
        if (cat != null)
        {
            SnippetCategories.Remove(cat);
            if (SelectedSnippetCategory == cat) SelectedSnippetCategory = SnippetCategories.FirstOrDefault();
            SaveSnippets();
        }
    }

    public void AddSnippetCategory(string name)
    {
        var cat = new SnippetCategory { Name = name };
        cat.SubCategories.Add(new SnippetSubCategory { Name = "Main" });
        SnippetCategories.Add(cat);
        SelectedSnippetCategory = cat;
        SaveSnippets();
    }

    public void AddSnippetSubCategory(string name)
    {
        if (SelectedSnippetCategory != null)
        {
            var sub = new SnippetSubCategory { Name = name };
            SelectedSnippetCategory.SubCategories.Add(sub);
            SelectedSnippetSubCategory = sub;
            SaveSnippets();
        }
    }

    public void AddSnippet(string desc, string cmd)
    {
        if (SelectedSnippetSubCategory != null && !string.IsNullOrWhiteSpace(cmd))
        {
            SelectedSnippetSubCategory.Snippets.Add(new SnippetItem { Description = string.IsNullOrWhiteSpace(desc) ? "Без описания" : desc, Command = cmd });
            SaveSnippets();
        }
    }

    public void InitializeWebView(WebView2 webView)
    {
        _logger.Log("TERM-WEBVIEW", "Старт инициализации WebView2...");
        _webView = webView;
        _webView.EnsureCoreWebView2Async().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                _logger.Log("TERM-WEBVIEW-ERR", $"Ошибка загрузки WebView2: {t.Exception?.InnerException?.Message}");
                return;
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                _webView.NavigateToString(GetTerminalHtml());
            });
        });
    }

    public async Task ConnectAsync()
    {
        if (CurrentProfile == null)
        {
            ConnectionInfo = "Ошибка: профиль не задан.";
            return;
        }

        if (!_sshService.IsConnected)
        {
            string result = await _sshService.ConnectAsync(CurrentProfile.IpAddress, CurrentProfile.Port, CurrentProfile.Username ?? "root", CurrentProfile.Password ?? "", CurrentProfile.KeyPath ?? "");
            if (result != "SUCCESS")
            {
                ConnectionInfo = $"ОШИБКА ПОДКЛЮЧЕНИЯ: {result}";
                return;
            }
        }

        IsConnected = _sshService.IsConnected;
        ConnectionInfo = $"ПОДКЛЮЧЕНО: {CurrentProfile.Username}@{CurrentProfile.IpAddress}";

        await LoadFilesAsync();

        StartMonitoring();

        try
        {
            _shellStream = _sshService.CreateShellStream("xterm-256color", 120, 40, 1200, 600, 1024);
            _readCts = new CancellationTokenSource();
            _ = ReadShellOutputAsync(_readCts.Token);
        }
        catch (Exception ex)
        {
            ConnectionInfo = $"ОШИБКА SHELL: {ex.Message}";
        }
    }

    public void SendManualCommand()
    {
        if (string.IsNullOrWhiteSpace(CommandInput) || _shellStream == null) return;

        try
        {
            _shellStream.Write(CommandInput + "\r");
            CommandInput = "";
        }
        catch { }
    }

    private void CoreWebView2_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string json = e.WebMessageAsJson;
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp))
            {
                string type = typeProp.GetString() ?? "";

                if (type == "ready")
                {
                    _isWebViewReady = true;
                    if (_outputBuffer.Length > 0)
                    {
                        string safeOutput = System.Text.Json.JsonSerializer.Serialize(_outputBuffer.ToString());
                        _webView?.ExecuteScriptAsync($"term.write({safeOutput});");
                        _outputBuffer.Clear();
                    }
                }
                else if (type == "input" && root.TryGetProperty("data", out var dataProp))
                {
                    _shellStream?.Write(dataProp.GetString());
                }
                else if (type == "resize")
                {
                    uint cols = root.GetProperty("cols").GetUInt32();
                    uint rows = root.GetProperty("rows").GetUInt32();
                    _sshService?.ResizeTerminal(cols, rows);
                }
            }
        }
        catch { }
    }

    private async Task ReadShellOutputAsync(CancellationToken token)
    {
        if (_shellStream == null || _webView == null) return;

        byte[] buffer = new byte[4096];

        try
        {
            while (!token.IsCancellationRequested)
            {
                int bytesRead = await _shellStream.ReadAsync(buffer, 0, buffer.Length, token);
                if (bytesRead > 0)
                {
                    string output = Encoding.UTF8.GetString(buffer, 0, bytesRead);

                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        if (_isWebViewReady)
                        {
                            string safeOutput = System.Text.Json.JsonSerializer.Serialize(output);
                            _webView.ExecuteScriptAsync($"term.write({safeOutput});");
                        }
                        else
                        {
                            _outputBuffer.Append(output);
                        }
                    });
                }
                else
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    [RelayCommand]
    private async Task LoadFilesAsync()
    {
        if (_sshService == null || !_sshService.IsConnected) return;

        try
        {
            var remoteFiles = await _sshService.ListDirectoryAsync(CurrentDirectory);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Files.Clear();

                // Добавляем "Назад" ТОЛЬКО если мы не в корне
                if (CurrentDirectory != "/")
                {
                    Files.Add(new RemoteFileItem { Name = "..", IsDirectory = true });
                }

                // ИСПРАВЛЕНИЕ: Игнорируем системные папки . и .. от самого сервера, чтобы не было дублей!
                foreach (var file in remoteFiles.Where(f => f.Name != "." && f.Name != ".."))
                {
                    Files.Add(new RemoteFileItem { Name = file.Name, IsDirectory = file.IsDir });
                }
            });
        }
        catch { }
    }

    [RelayCommand]
    private async Task NavigateAsync(RemoteFileItem? item)
    {
        if (item == null) return;

        if (!item.IsDirectory)
        {
            await OpenFileForEditAsync(item);
            return;
        }

        if (item.Name == "..")
        {
            var parts = CurrentDirectory.TrimEnd('/').Split('/');
            if (parts.Length > 1)
            {
                CurrentDirectory = string.Join("/", parts.Take(parts.Length - 1));
                if (string.IsNullOrEmpty(CurrentDirectory)) CurrentDirectory = "/";
            }
        }
        else
        {
            CurrentDirectory = CurrentDirectory == "/" ? $"/{item.Name}" : $"{CurrentDirectory}/{item.Name}";
        }

        await LoadFilesAsync();
    }

    private async Task OpenFileForEditAsync(RemoteFileItem item)
    {
        if (_sshService == null || !_sshService.IsConnected) return;

        string remotePath = CurrentDirectory.EndsWith("/") ? CurrentDirectory + item.Name : CurrentDirectory + "/" + item.Name;

        string tempDir = Path.Combine(Path.GetTempPath(), "KoFFPanel_Editor");
        Directory.CreateDirectory(tempDir);
        string localPath = Path.Combine(tempDir, item.Name);

        try
        {
            _logger.Log("TERM-SFTP", $"Скачивание файла для редактора: {remotePath}");
            using (var fileStream = File.Create(localPath))
            {
                await _sshService.DownloadFileAsync(remotePath, fileStream);
            }

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                OnFileReadyForEdit?.Invoke(localPath, remotePath);
            });
        }
        catch (Exception ex)
        {
            _logger.Log("TERM-SFTP-ERR", $"Ошибка скачивания: {ex.Message}");
        }
    }

    public async Task UploadEditedFileAsync(string localPath, string remotePath)
    {
        if (_sshService == null || !_sshService.IsConnected) return;

        try
        {
            _logger.Log("TERM-SFTP", $"Выгрузка файла на сервер: {remotePath}");
            using (var fileStream = File.OpenRead(localPath))
            {
                await Task.Run(() => _sshService.UploadFile(fileStream, remotePath));
            }
            _logger.Log("TERM-SFTP", $"Файл {remotePath} успешно сохранен!");
        }
        catch (Exception ex)
        {
            _logger.Log("TERM-SFTP-ERR", $"Ошибка выгрузки: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task RenameItemAsync((string OldName, string NewName) tuple)
    {
        if (_sshService == null || !_sshService.IsConnected || string.IsNullOrEmpty(tuple.OldName) || string.IsNullOrEmpty(tuple.NewName) || tuple.OldName == tuple.NewName) return;

        string basePath = CurrentDirectory.EndsWith("/") ? CurrentDirectory : CurrentDirectory + "/";
        string oldPath = basePath + tuple.OldName;
        string newPath = basePath + tuple.NewName;

        string safeOld = oldPath.Replace("'", "'\\''");
        string safeNew = newPath.Replace("'", "'\\''");

        try
        {
            await _sshService.ExecuteCommandAsync($"mv '{safeOld}' '{safeNew}'");
            _logger.Log("TERM-SFTP", $"Переименовано: {oldPath} -> {newPath}");
            await LoadFilesAsync();
        }
        catch (Exception ex) { _logger.Log("TERM-SFTP-ERR", $"Ошибка переименования: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task DeleteItemAsync(RemoteFileItem item)
    {
        if (_sshService == null || !_sshService.IsConnected || item == null || item.Name == "..") return;

        string fullPath = CurrentDirectory.EndsWith("/") ? CurrentDirectory + item.Name : CurrentDirectory + "/" + item.Name;
        string safePath = fullPath.Replace("'", "'\\''");

        try
        {
            await _sshService.ExecuteCommandAsync($"rm -rf '{safePath}'");
            _logger.Log("TERM-SFTP", $"Удалено: {fullPath}");
            await LoadFilesAsync();
        }
        catch (Exception ex) { _logger.Log("TERM-SFTP-ERR", $"Ошибка удаления: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task CreateItemAsync((string Name, bool IsFolder) tuple)
    {
        if (_sshService == null || !_sshService.IsConnected || string.IsNullOrEmpty(tuple.Name)) return;

        string fullPath = CurrentDirectory.EndsWith("/") ? CurrentDirectory + tuple.Name : CurrentDirectory + "/" + tuple.Name;
        string safePath = fullPath.Replace("'", "'\\''");

        try
        {
            if (tuple.IsFolder) await _sshService.ExecuteCommandAsync($"mkdir -p '{safePath}'");
            else await _sshService.ExecuteCommandAsync($"touch '{safePath}'");

            _logger.Log("TERM-SFTP", $"Создано (IsFolder: {tuple.IsFolder}): {fullPath}");
            await LoadFilesAsync();
        }
        catch (Exception ex) { _logger.Log("TERM-SFTP-ERR", $"Ошибка создания: {ex.Message}"); }
    }

    public void Dispose()
    {
        _monitoringTimer?.Stop();
        _monitoringTimer?.Dispose();
        _readCts?.Cancel();
        _readCts?.Dispose();
        _shellStream?.Dispose();
    }

    private void StartMonitoring()
    {
        _monitoringTimer?.Stop();
        _monitoringTimer?.Dispose();

        _monitoringTimer = new System.Timers.Timer(5000); // Раз в 5 секунд
        _monitoringTimer.Elapsed += async (s, e) => await UpdateServerStatsAsync();
        _monitoringTimer.Start();

        // Дергаем первый раз сразу, не дожидаясь 5 секунд
        _ = UpdateServerStatsAsync();
    }

    private async Task UpdateServerStatsAsync()
    {
        if (_sshService == null || !_sshService.IsConnected || CurrentProfile == null) return;

        try
        {
            // Получаем пинг
            var pingResult = await _monitorService.PingServerAsync(CurrentProfile.IpAddress);
            if (pingResult.Success)
            {
                PingMs = pingResult.RoundtripTime;
            }

            // Получаем ресурсы. Используем 'sing-box' как дефолт, т.к. мы просто запрашиваем CPU/RAM
            var stats = await _monitorService.GetResourcesAsync(_sshService, "sing-box");

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                CpuUsage = stats.Cpu;
                RamUsage = stats.Ram;
                SsdUsage = stats.Ssd;
            });
        }
        catch (Exception ex)
        {
            _logger.Log("TERM-MONITOR-ERR", $"Ошибка обновления статистики: {ex.Message}");
        }
    }

    private string GetTerminalHtml()
    {
        return @"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"" />
    <link rel=""stylesheet"" href=""https://cdn.jsdelivr.net/npm/xterm@5.3.0/css/xterm.css"" />
    <script src=""https://cdn.jsdelivr.net/npm/xterm@5.3.0/lib/xterm.js""></script>
    <script src=""https://cdn.jsdelivr.net/npm/xterm-addon-fit@0.8.0/lib/xterm-addon-fit.js""></script>
    <style>
        /* Делаем HTML и Body абсолютно прозрачными, чтобы просвечивал градиент из WPF */
        body, html { 
            margin: 0; padding: 0; width: 100%; height: 100%; 
            background-color: transparent !important; 
            overflow: hidden; 
        }
        
        #terminal { 
            height: 100%; width: 100%; padding: 12px 12px 0 12px; 
            box-sizing: border-box; background: transparent !important; 
        }
        
        /* Жесткое подавление черного фона во всех слоях xterm.js */
        .xterm, .xterm-viewport, .xterm-screen, .xterm-text-layer, .xterm-canvas-layer { 
            background-color: transparent !important; 
        }
        
        .xterm-viewport::-webkit-scrollbar { display: none; }
        .xterm-viewport { -ms-overflow-style: none; scrollbar-width: none; }
    </style>
</head>
<body>
    <div id=""terminal""></div>
    <script>
        const term = new Terminal({
            allowTransparency: true, /* ИСПРАВЛЕНИЕ: Жизненно важно для прозрачности канваса */
            theme: { 
                background: '#00000000', /* ИСПРАВЛЕНИЕ: Строгий HEX с нулем альфа-канала */
                foreground: '#d4d4d4', 
                cursor: '#569cd6', 
                selectionBackground: 'rgba(38, 79, 120, 0.5)' 
            },
            cursorBlink: true, fontSize: 15, fontFamily: ""'Cascadia Code', Consolas, 'Courier New', monospace"", scrollback: 5000
        });

        const fitAddon = new FitAddon.FitAddon();
        term.loadAddon(fitAddon);
        term.open(document.getElementById('terminal'));
        
        function resizeTerminal() {
            fitAddon.fit();
            window.chrome.webview.postMessage({ type: 'resize', cols: term.cols, rows: term.rows });
        }

        setTimeout(resizeTerminal, 100);
        let resizeTimeout;
        window.addEventListener('resize', () => { clearTimeout(resizeTimeout); resizeTimeout = setTimeout(resizeTerminal, 100); });
        term.onData(data => { window.chrome.webview.postMessage({ type: 'input', data: data }); });
        
        window.chrome.webview.postMessage({ type: 'ready' });
    </script>
</body>
</html>";
    }

    public partial class SnippetItem : ObservableObject
    {
        [ObservableProperty] private string _description = "";
        [ObservableProperty] private string _command = "";
    }

    public partial class SnippetSubCategory : ObservableObject
    {
        [ObservableProperty] private string _name = "";
        [ObservableProperty] private ObservableCollection<SnippetItem> _snippets = new();
    }

    public partial class SnippetCategory : ObservableObject
    {
        [ObservableProperty] private string _name = "";
        [ObservableProperty] private ObservableCollection<SnippetSubCategory> _subCategories = new();
    }
}