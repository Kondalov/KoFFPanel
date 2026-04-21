using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Services;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.Features.Terminal;

public partial class TerminalViewModel : ObservableObject, IDisposable
{
    private readonly ISshService _sshService;
    public ISshService SshService => _sshService;
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

    [RelayCommand]
    private void InsertSnippet(SnippetItem item)
    {
        if (item != null) CommandInput = item.Command;
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

    public void SendManualCommand()
    {
        if (string.IsNullOrWhiteSpace(CommandInput) || _shellStream == null) return;
        try { _shellStream.Write(CommandInput + "\r"); CommandInput = ""; } catch { }
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
                else if (type == "input" && root.TryGetProperty("data", out var dataProp)) { _shellStream?.Write(dataProp.GetString()); }
                else if (type == "resize")
                {
                    uint cols = root.GetProperty("cols").GetUInt32();
                    uint rows = root.GetProperty("rows").GetUInt32();
                    _sshService?.ResizeTerminal(cols, rows);
                }
            }
        } catch { }
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
                if (CurrentDirectory != "/") Files.Add(new RemoteFileItem { Name = "..", IsDirectory = true });
                foreach (var file in remoteFiles.Where(f => f.Name != "." && f.Name != ".."))
                    Files.Add(new RemoteFileItem { Name = file.Name, IsDirectory = file.IsDir });
            });
        } catch { }
    }

    [RelayCommand]
    private async Task NavigateAsync(RemoteFileItem? item)
    {
        if (item == null) return;
        if (!item.IsDirectory) { await OpenFileForEditAsync(item); return; }
        if (item.Name == "..")
        {
            var parts = CurrentDirectory.TrimEnd('/').Split('/');
            if (parts.Length > 1)
            {
                CurrentDirectory = string.Join("/", parts.Take(parts.Length - 1));
                if (string.IsNullOrEmpty(CurrentDirectory)) CurrentDirectory = "/";
            }
        }
        else { CurrentDirectory = CurrentDirectory == "/" ? $"/{item.Name}" : $"{CurrentDirectory}/{item.Name}"; }
        await LoadFilesAsync();
    }

    public async Task UploadEditedFileAsync(string localPath, string remotePath)
    {
        if (_sshService == null || !_sshService.IsConnected) return;
        try
        {
            _logger.Log("TERM-SFTP", $"Выгрузка файла на сервер: {remotePath}");
            using (var fileStream = File.OpenRead(localPath)) { await Task.Run(() => _sshService.UploadFile(fileStream, remotePath)); }
            string fileDir = Path.GetDirectoryName(remotePath)?.Replace("\\", "/") ?? "/";
            string currentDirNorm = CurrentDirectory.TrimEnd('/');
            string fileDirNorm = fileDir.TrimEnd('/');
            if (string.IsNullOrEmpty(currentDirNorm)) currentDirNorm = "/";
            if (string.IsNullOrEmpty(fileDirNorm)) fileDirNorm = "/";
            if (currentDirNorm == fileDirNorm) await LoadFilesAsync();
        } catch (Exception ex) { _logger.Log("TERM-SFTP-ERR", $"Ошибка выгрузки: {ex.Message}"); throw; }
    }

    [RelayCommand]
    private async Task RenameItemAsync((string OldName, string NewName) tuple)
    {
        if (_sshService == null || !_sshService.IsConnected || string.IsNullOrEmpty(tuple.OldName) || string.IsNullOrEmpty(tuple.NewName) || tuple.OldName == tuple.NewName) return;
        string basePath = CurrentDirectory.EndsWith("/") ? CurrentDirectory : CurrentDirectory + "/";
        string oldPath = basePath + tuple.OldName;
        string newPath = basePath + tuple.NewName;
        try { await _sshService.ExecuteCommandAsync($"mv {SshGuard.Escape(oldPath)} {SshGuard.Escape(newPath)}"); await LoadFilesAsync(); }
        catch (Exception ex) { _logger.Log("TERM-SFTP-ERR", $"Ошибка переименования: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task DeleteItemAsync(RemoteFileItem item)
    {
        if (_sshService == null || !_sshService.IsConnected || item == null || item.Name == "..") return;
        string fullPath = CurrentDirectory.EndsWith("/") ? CurrentDirectory + item.Name : CurrentDirectory + "/" + item.Name;
        try { await _sshService.ExecuteCommandAsync($"rm -rf {SshGuard.Escape(fullPath)}"); await LoadFilesAsync(); }
        catch (Exception ex) { _logger.Log("TERM-SFTP-ERR", $"Ошибка удаления: {ex.Message}"); }
    }

    [RelayCommand]
    private async Task CreateItemAsync((string Name, bool IsFolder) tuple)
    {
        if (_sshService == null || !_sshService.IsConnected || string.IsNullOrEmpty(tuple.Name)) return;
        string fullPath = CurrentDirectory.EndsWith("/") ? CurrentDirectory + tuple.Name : CurrentDirectory + "/" + tuple.Name;
        try {
            if (tuple.IsFolder) await _sshService.ExecuteCommandAsync($"mkdir -p {SshGuard.Escape(fullPath)}");
            else await _sshService.ExecuteCommandAsync($"touch {SshGuard.Escape(fullPath)}");
            await LoadFilesAsync();
        } catch (Exception ex) { _logger.Log("TERM-SFTP-ERR", $"Ошибка создания: {ex.Message}"); }
    }

    public void Dispose()
    {
        _monitoringTimer?.Stop();
        _monitoringTimer?.Dispose();
        _readCts?.Cancel();
        _readCts?.Dispose();
        _shellStream?.Dispose();
    }
}
