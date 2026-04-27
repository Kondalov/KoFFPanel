using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using System.Linq;
using KoFFPanel.Domain.Entities;
using System.Text;
using System.Threading;

namespace KoFFPanel.Presentation.Features.Terminal;

public partial class TerminalViewModel
{
    public void LoadSnippets()
    {
        try
        {
            string filePath = GetSnippetsFilePath();

            if (File.Exists(filePath))
            {
                string json = File.ReadAllText(filePath);
                var data = JsonSerializer.Deserialize<System.Collections.ObjectModel.ObservableCollection<SnippetCategory>>(json);
                if (data != null)
                {
                    SnippetCategories = data;
                }
                _logger.Log("TERM-SNIP", "Сниппеты успешно загружены из папки Snippets.");
            }
            else
            {
                var cat = new SnippetCategory { Name = "Server" };
                var sub = new SnippetSubCategory { Name = "Общие" };
                sub.Snippets.Add(new SnippetItem { Description = "Обновление системы", Command = "apt update && apt upgrade -y" });
                cat.SubCategories.Add(sub);
                SnippetCategories.Add(cat);
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

            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(SnippetCategories, options);

            File.WriteAllText(filePath, json);
        }
        catch (Exception ex)
        {
            _logger.Log("TERM-SNIP-ERR", $"Ошибка сохранения: {ex.Message}");
        }
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

        // === ИСПРАВЛЕНИЕ: Динамическая стартовая директория (Защита от дурака) ===
        try
        {
            string homeDir = await _sshService.ExecuteCommandAsync("pwd");
            CurrentDirectory = string.IsNullOrWhiteSpace(homeDir) ? "/" : homeDir.Trim();
        }
        catch
        {
            CurrentDirectory = "/";
        }

        await LoadFilesAsync();

        StartMonitoring();

        try
        {
            _shellStream = _sshService.CreateShellStream("xterm-256color", 120, 40, 1200, 600, 1024);
            _readCts = new CancellationTokenSource();
            SafeFireAndForget(() => ReadShellOutputAsync(_readCts.Token));
        }
        catch (Exception ex)
        {
            ConnectionInfo = $"ОШИБКА SHELL: {ex.Message}";
        }
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

                    System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                    {
                        if (_isWebViewReady)
                        {
                            string safeOutput = JsonSerializer.Serialize(output);
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

    private void StartMonitoring()
    {
        _monitoringTimer?.Stop();
        _monitoringTimer?.Dispose();
        _monitoringTimer = new System.Timers.Timer(5000);
        _monitoringTimer.Elapsed += (s, e) => SafeFireAndForget(() => UpdateServerStatsAsync());
        _monitoringTimer.Start();
        SafeFireAndForget(() => UpdateServerStatsAsync());
    }

    private async Task UpdateServerStatsAsync()
    {
        if (_sshService == null || !_sshService.IsConnected || CurrentProfile == null) return;

        try
        {
            var pingResult = await _monitorService.PingServerAsync(CurrentProfile.IpAddress);
            if (pingResult.Success)
            {
                PingMs = pingResult.RoundtripTime;
            }

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

    private async void SafeFireAndForget(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger?.Log("TERM-CRASH-PREVENTED", $"Предотвращено падение приложения в фоне: {ex.Message}");
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
        body, html { margin: 0; padding: 0; width: 100%; height: 100%; background-color: transparent !important; overflow: hidden; }
        #terminal { height: 100%; width: 100%; padding: 12px 12px 0 12px; box-sizing: border-box; background: transparent !important; }
        .xterm, .xterm-viewport, .xterm-screen, .xterm-text-layer, .xterm-canvas-layer { background-color: transparent !important; }
        .xterm-viewport::-webkit-scrollbar { display: none; }
        .xterm-viewport { -ms-overflow-style: none; scrollbar-width: none; }
    </style>
</head>
<body>
    <div id=""terminal""></div>
    <script>
        const term = new Terminal({
            allowTransparency: true,
            theme: { background: '#00000000', foreground: '#d4d4d4', cursor: '#569cd6', selectionBackground: 'rgba(38, 79, 120, 0.5)' },
            cursorBlink: true, fontSize: 15, fontFamily: ""'Cascadia Code', Consolas, 'Courier New', monospace"", scrollback: 5000
        });
        const fitAddon = new FitAddon.FitAddon();
        term.loadAddon(fitAddon);
        term.open(document.getElementById('terminal'));
        function resizeTerminal() { fitAddon.fit(); window.chrome.webview.postMessage({ type: 'resize', cols: term.cols, rows: term.rows }); }
        setTimeout(resizeTerminal, 100);
        let resizeTimeout;
        window.addEventListener('resize', () => { clearTimeout(resizeTimeout); resizeTimeout = setTimeout(resizeTerminal, 100); });
        term.onData(data => { window.chrome.webview.postMessage({ type: 'input', data: data }); });
        window.chrome.webview.postMessage({ type: 'ready' });
    </script>
</body>
</html>";
    }
}
