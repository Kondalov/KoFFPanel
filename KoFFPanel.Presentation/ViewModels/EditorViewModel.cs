using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.ViewModels;

public partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly IAppLogger _logger;
    private WebView2? _webView;

    // Пути для работы с файлом
    private string _localFilePath = "";
    public string RemoteFilePath { get; private set; } = "";

    [ObservableProperty] private string _windowTitle = "Редактор";
    [ObservableProperty] private string _saveStatus = "";
    [ObservableProperty] private string _statusColor = "#a0aabf";

    public bool HasUnsavedChanges { get; private set; } = false;

    // Делегат для уведомления главного окна о необходимости загрузить файл обратно на сервер
    public Action<string, string>? OnSaveRequested;

    public EditorViewModel(IAppLogger logger)
    {
        _logger = logger;
    }

    public void Initialize(string localFilePath, string remoteFilePath)
    {
        _localFilePath = localFilePath;
        RemoteFilePath = remoteFilePath;
        WindowTitle = $"Редактирование: {Path.GetFileName(remoteFilePath)}";
    }

    public void InitializeWebView(WebView2 webView)
    {
        _webView = webView;
        _webView.EnsureCoreWebView2Async().ContinueWith(t =>
        {
            if (t.IsFaulted) return;

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                _webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                _webView.NavigateToString(GetMonacoHtml());
            });
        });
    }

    private void CoreWebView2_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            string json = e.WebMessageAsJson;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp))
            {
                string type = typeProp.GetString() ?? "";

                if (type == "ready")
                {
                    // Редактор загрузился, отправляем ему содержимое файла
                    LoadFileContentIntoEditor();
                }
                else if (type == "content_changed")
                {
                    // Пользователь что-то напечатал
                    HasUnsavedChanges = true;
                    SaveStatus = "Не сохранено *";
                    StatusColor = "#ffaa00";
                }
                else if (type == "save_request")
                {
                    // Нажата кнопка Ctrl+S внутри редактора
                    string content = root.GetProperty("content").GetString() ?? "";
                    PerformSave(content);
                }
            }
        }
        catch (Exception ex) { _logger.Log("EDITOR-ERR", $"Ошибка IPC: {ex.Message}"); }
    }

    private void LoadFileContentIntoEditor()
    {
        try
        {
            if (File.Exists(_localFilePath))
            {
                string content = File.ReadAllText(_localFilePath);
                string extension = Path.GetExtension(_localFilePath).ToLower();

                // Простая эвристика для определения языка
                string language = "plaintext";
                if (extension == ".json") language = "json";
                else if (extension == ".sh" || extension == ".bash") language = "shell";
                else if (extension == ".yaml" || extension == ".yml") language = "yaml";
                else if (extension == ".xml") language = "xml";
                else if (extension == ".js") language = "javascript";

                string safeContent = JsonSerializer.Serialize(content);

                _webView?.ExecuteScriptAsync($@"
                    window.editor.setValue({safeContent});
                    monaco.editor.setModelLanguage(window.editor.getModel(), '{language}');
                ");
            }
        }
        catch (Exception ex) { _logger.Log("EDITOR-ERR", $"Ошибка чтения файла: {ex.Message}"); }
    }

    [RelayCommand]
    private void Save()
    {
        // Просим JS прислать нам текущий текст
        _webView?.ExecuteScriptAsync(@"
            window.chrome.webview.postMessage({ 
                type: 'save_request', 
                content: window.editor.getValue() 
            });
        ");
    }

    [RelayCommand]
    private void Undo() => _webView?.ExecuteScriptAsync("window.editor.trigger('keyboard', 'undo', null);");

    [RelayCommand]
    private void Redo() => _webView?.ExecuteScriptAsync("window.editor.trigger('keyboard', 'redo', null);");

    private void PerformSave(string content)
    {
        try
        {
            // Сохраняем локально
            File.WriteAllText(_localFilePath, content);
            HasUnsavedChanges = false;

            SaveStatus = "Сохранено ✓";
            StatusColor = "#00ff88";

            // Сообщаем главному окну, что пора отправлять файл по SFTP
            OnSaveRequested?.Invoke(_localFilePath, RemoteFilePath);
        }
        catch (Exception ex)
        {
            SaveStatus = "Ошибка сохранения!";
            StatusColor = "#ff4444";
            _logger.Log("EDITOR-ERR", $"Ошибка записи: {ex.Message}");
        }
    }

    public void Dispose() { }

    // Вшитый HTML-шаблон Monaco Editor (загружается через CDN)
    private string GetMonacoHtml()
    {
        return @"
<!DOCTYPE html>
<html>
<head>
    <meta charset=""utf-8"">
    <style>
        html, body, #container { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background-color: #1e1e1e; }
    </style>
</head>
<body>
    <div id=""container""></div>
    <script src=""https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.39.0/min/vs/loader.min.js""></script>
    <script>
        require.config({ paths: { 'vs': 'https://cdnjs.cloudflare.com/ajax/libs/monaco-editor/0.39.0/min/vs' }});
        require(['vs/editor/editor.main'], function() {
            
            // Настраиваем темную тему, похожую на VS Code
            window.editor = monaco.editor.create(document.getElementById('container'), {
                value: 'Загрузка...',
                language: 'plaintext',
                theme: 'vs-dark',
                automaticLayout: true,
                fontSize: 14,
                fontFamily: ""'Cascadia Code', Consolas, monospace"",
                minimap: { enabled: true },
                scrollBeyondLastLine: false,
                wordWrap: 'on'
            });

            // Отслеживаем изменения
            window.editor.onDidChangeModelContent(() => {
                window.chrome.webview.postMessage({ type: 'content_changed' });
            });

            // Биндим Ctrl+S
            window.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, function() {
                window.chrome.webview.postMessage({ 
                    type: 'save_request', 
                    content: window.editor.getValue() 
                });
            });

            // Сообщаем C#, что мы готовы принять текст
            window.chrome.webview.postMessage({ type: 'ready' });
        });
    </script>
</body>
</html>";
    }
}