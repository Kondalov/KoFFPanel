using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Presentation.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Renci.SshNet;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

namespace KoFFPanel.Presentation.Views;

public partial class TerminalWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly TerminalViewModel _viewModel;
    private readonly IServiceProvider _serviceProvider;

    // Переменные для живого потока терминала
    private ShellStream? _shellStream;
    private CancellationTokenSource? _shellCts;

    public TerminalWindow(TerminalViewModel viewModel, IServiceProvider serviceProvider)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _serviceProvider = serviceProvider;
        DataContext = _viewModel;

        InitializeWebViewAsync();

        // Обязательно очищаем ресурсы при закрытии окна
        this.Closed += TerminalWindow_Closed;
    }

    private async void InitializeWebViewAsync()
    {
        try
        {
            await TerminalWebView.EnsureCoreWebView2Async(null);

            string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwow", "Terminal.html");
            TerminalWebView.CoreWebView2.Navigate(htmlPath);

            var sshService = _serviceProvider.GetRequiredService<ISshService>();

            if (_viewModel.CurrentProfile != null)
            {
                await sshService.ConnectAsync(_viewModel.CurrentProfile.IpAddress, _viewModel.CurrentProfile.Port, _viewModel.CurrentProfile.Username, _viewModel.CurrentProfile.Password, _viewModel.CurrentProfile.KeyPath);
                await _viewModel.SetSshSessionAsync(sshService);

                // СОЗДАЕМ ЖИВОЙ ПОТОК ТЕРМИНАЛА (120 колонок, 40 строк - для адаптивного шрифта)
                _shellStream = sshService.CreateShellStream("xterm", 120, 40, 800, 600, 4096);
                _shellCts = new CancellationTokenSource();

                // Запускаем бесконечный цикл чтения ответов от Linux
                _ = ReadShellAsync();
            }

            // Перехватываем нажатия клавиш прямо внутри черного экрана xterm.js (например, стрелочки)
            TerminalWebView.CoreWebView2.WebMessageReceived += (s, e) =>
            {
                string? inputFromJs = e.TryGetWebMessageAsString();
                if (!string.IsNullOrEmpty(inputFromJs) && _shellStream != null && _shellStream.CanWrite)
                {
                    _shellStream.Write(inputFromJs);
                    _shellStream.Flush();
                }
            };
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка инициализации SSH потока: {ex.Message}", "Критическая ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // --- ФОНОВЫЙ ЧТЕЦ LINUX ---
    private async Task ReadShellAsync()
    {
        var buffer = new byte[4096];
        try
        {
            while (_shellCts != null && !_shellCts.Token.IsCancellationRequested)
            {
                if (_shellStream == null) break;

                int read = await _shellStream.ReadAsync(buffer, 0, buffer.Length, _shellCts.Token);
                if (read > 0)
                {
                    string text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

                    // ИСПРАВЛЕНИЕ: Используем полный путь System.Windows.Application, чтобы избежать конфликта неймспейсов
                    System.Windows.Application.Current.Dispatcher.Invoke(() =>
                    {
                        TerminalWebView.CoreWebView2?.PostWebMessageAsString(text);
                    });
                }
            }
        }
        catch (TaskCanceledException)
        {
            // ИСПРАВЛЕНИЕ: Убрали пустой catch, теперь пишем в лог отладки
            Debug.WriteLine("Поток терминала остановлен.");
        }
        catch (Exception ex)
        {
            // ИСПРАВЛЕНИЕ: Убрали пустой catch
            Debug.WriteLine($"Потеря соединения в терминале: {ex.Message}");
        }
    }

    // --- ОБРАБОТЧИК ДВОЙНОГО КЛИКА (ПРОВОДНИК) ---
    private void ExplorerItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is System.Windows.Controls.ListViewItem item && item.DataContext is RemoteFileItem fileItem)
        {
            if (_viewModel.NavigateCommand.CanExecute(fileItem))
                _viewModel.NavigateCommand.Execute(fileItem);
        }
    }

    // --- СТРОКА ВВОДА СНИЗУ ---
    private void SendCommand_Click(object sender, RoutedEventArgs e)
    {
        ExecuteCommand();
    }

    private void CommandTextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ExecuteCommand();
        }
    }

    private void ExecuteCommand()
    {
        if (string.IsNullOrWhiteSpace(_viewModel.CommandInput) || _shellStream == null) return;

        // Отправляем команду в Linux + имитация нажатия Enter (\r)
        _shellStream.WriteLine(_viewModel.CommandInput);

        _viewModel.CommandInput = ""; // Очищаем поле

        // Возвращаем фокус в Xterm (чтобы сразу видеть результат)
        TerminalWebView.Focus();
    }

    // --- ОЧИСТКА ПАМЯТИ ---
    private void TerminalWindow_Closed(object? sender, EventArgs e)
    {
        _shellCts?.Cancel();
        _shellCts?.Dispose();
        _shellStream?.Dispose();
    }
}