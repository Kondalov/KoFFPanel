using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.Features.Terminal;

public partial class EditorViewModel : ObservableObject, IDisposable
{
    private readonly IAppLogger _logger;
    private readonly ISshService _sshService; // Добавлено для верификации
    private WebView2? _webView;

    private string _localFilePath = "";
    public string RemoteFilePath { get; private set; } = "";

    [ObservableProperty] private string _windowTitle = "Редактор";

    // Статус в тулбаре
    [ObservableProperty] private string _fileStateStatus = "";
    [ObservableProperty] private string _fileStateColor = "#cccccc";

    // Статус-бар (Внизу)
    [ObservableProperty] private string _cursorPosition = "Стр 1, Стб 1";
    [ObservableProperty] private string _editorLanguage = "plaintext";
    [ObservableProperty] private bool _isSaving = false;
    [ObservableProperty] private bool _isSaveComplete = false;
    [ObservableProperty] private string _verifyMessage = "";
    [ObservableProperty] private string _verifyIcon = "Checkmark24";

    public bool HasUnsavedChanges { get; private set; } = false;

    // Делегат сохранения (теперь возвращает Task, чтобы мы могли дождаться загрузки)
    public Func<string, string, Task<bool>>? OnSaveRequested;

    // Делегат для UI "Сохранить как"
    public Action<string>? OnSaveAsRequested;

    public EditorViewModel(IAppLogger logger, ISshService sshService)
    {
        _logger = logger;
        _sshService = sshService;
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
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("type", out var typeProp))
            {
                string type = typeProp.GetString() ?? "";

                if (type == "ready") LoadFileContentIntoEditor();
                else if (type == "content_changed")
                {
                    HasUnsavedChanges = true;
                    FileStateStatus = "Не сохранено *";
                    FileStateColor = "#ffaa00";
                    IsSaveComplete = false; // Скрываем галочку сохранения
                }
                else if (type == "save_request")
                {
                    _ = PerformSmartSaveAsync(root.GetProperty("content").GetString() ?? "", RemoteFilePath);
                }
                else if (type == "cursor_moved")
                {
                    // Обновляем позицию курсора в статус-баре
                    int line = root.GetProperty("line").GetInt32();
                    int col = root.GetProperty("column").GetInt32();
                    CursorPosition = $"Стр {line}, Стб {col}";
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

                EditorLanguage = extension switch
                {
                    ".json" => "json",
                    ".sh" or ".bash" => "shell",
                    ".yaml" or ".yml" => "yaml",
                    ".xml" => "xml",
                    ".js" => "javascript",
                    _ => "plaintext"
                };

                string safeContent = JsonSerializer.Serialize(content);
                _webView?.ExecuteScriptAsync($@"
                    window.editor.setValue({safeContent});
                    monaco.editor.setModelLanguage(window.editor.getModel(), '{EditorLanguage}');
                ");
            }
        }
        catch { }
    }

    [RelayCommand] private void Save() => RequestContentFromEditor();
    [RelayCommand] private void Undo() => _webView?.ExecuteScriptAsync("window.editor.trigger('keyboard', 'undo', null);");
    [RelayCommand] private void Redo() => _webView?.ExecuteScriptAsync("window.editor.trigger('keyboard', 'redo', null);");

    // ИСПРАВЛЕНИЕ: Вызов встроенного поиска Monaco (Ctrl+F)
    [RelayCommand] private void Search() => _webView?.ExecuteScriptAsync("window.editor.getAction('actions.find').run();");

    // ИСПРАВЛЕНИЕ: Кнопка Сохранить Как
    [RelayCommand] private void SaveAs() => OnSaveAsRequested?.Invoke(Path.GetFileName(RemoteFilePath));

    private void RequestContentFromEditor()
    {
        _webView?.ExecuteScriptAsync("window.chrome.webview.postMessage({ type: 'save_request', content: window.editor.getValue() });");
    }

    // Вызывается из View, когда пользователь ввел новое имя в диалоге "Сохранить как"
    public void PerformSaveAs(string newFileName)
    {
        string directory = Path.GetDirectoryName(RemoteFilePath)?.Replace("\\", "/") ?? "/root";
        if (!directory.EndsWith("/")) directory += "/";

        string newRemotePath = directory + newFileName;
        RemoteFilePath = newRemotePath; // Обновляем путь
        WindowTitle = $"Редактирование: {newFileName}";

        RequestContentFromEditor(); // Запускаем цикл сохранения
    }

    // === УМНЫЙ АЛГОРИТМ СОХРАНЕНИЯ И ПРОВЕРКИ ===
    private async Task PerformSmartSaveAsync(string content, string targetRemotePath)
    {
        try
        {
            // 1. UI: Показываем лоадер
            IsSaving = true;
            IsSaveComplete = false;

            // 2. Валидация JSON (Защита от дурака)
            if (EditorLanguage == "json")
            {
                try { JsonDocument.Parse(content); }
                catch (JsonException)
                {
                    SetVerifyResult(false, "Ошибка: Невалидный JSON! Сервер может не запуститься.");
                    return;
                }
            }

            // 3. Сохраняем локально
            File.WriteAllText(_localFilePath, content);
            HasUnsavedChanges = false;
            FileStateStatus = ""; // Очищаем звездочку вверху

            // 4. Отправляем главному окну запрос на загрузку (SFTP)
            if (OnSaveRequested != null)
            {
                bool uploadSuccess = await OnSaveRequested.Invoke(_localFilePath, targetRemotePath);

                if (uploadSuccess)
                {
                    // 5. ВЕРИФИКАЦИЯ: Сверяем размер файла локально и на сервере
                    long localSize = new FileInfo(_localFilePath).Length;
                    string remoteSizeStr = await _sshService.ExecuteCommandAsync($"stat -c %s '{targetRemotePath}'");

                    if (long.TryParse(remoteSizeStr.Trim(), out long remoteSize) && localSize == remoteSize)
                    {
                        SetVerifyResult(true, $"✓ Успешно сохранено и проверено ({DateTime.Now:HH:mm:ss})");
                    }
                    else
                    {
                        SetVerifyResult(false, "Внимание! Размер файла на сервере не совпадает. Сохранение прервано.");
                    }
                }
                else
                {
                    SetVerifyResult(false, "Ошибка SFTP: Не удалось загрузить файл.");
                }
            }
        }
        catch (Exception ex)
        {
            SetVerifyResult(false, $"Системная ошибка: {ex.Message}");
            _logger.Log("EDITOR-ERR", $"Ошибка умного сохранения: {ex.Message}");
        }
    }

    private void SetVerifyResult(bool isSuccess, string message)
    {
        IsSaving = false;
        IsSaveComplete = true;
        VerifyIcon = isSuccess ? "Checkmark24" : "Warning24";
        VerifyMessage = message;
        if (!isSuccess) FileStateStatus = "Ошибка сохранения!";
    }

    public void Dispose() { }

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

            window.editor.onDidChangeModelContent(() => {
                window.chrome.webview.postMessage({ type: 'content_changed' });
            });

            // ИСПРАВЛЕНИЕ: Отслеживание позиции курсора
            window.editor.onDidChangeCursorPosition((e) => {
                window.chrome.webview.postMessage({ 
                    type: 'cursor_moved', 
                    line: e.position.lineNumber,
                    column: e.position.column
                });
            });

            window.editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyS, function() {
                window.chrome.webview.postMessage({ 
                    type: 'save_request', 
                    content: window.editor.getValue() 
                });
            });

            window.chrome.webview.postMessage({ type: 'ready' });
        });
    </script>
</body>
</html>";
    }
}