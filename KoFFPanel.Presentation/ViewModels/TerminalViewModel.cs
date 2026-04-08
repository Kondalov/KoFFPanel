using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using Renci.SshNet;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.ViewModels;

public partial class TerminalViewModel : ObservableObject
{
    private ISshService? _sshService;

    [ObservableProperty] private string _windowTitle = "SSH Терминал";
    [ObservableProperty] private string _connectionInfo = "Ожидание...";

    // Свойства для шапки (Статистика)
    [ObservableProperty] private int _cpuUsage = 0;
    [ObservableProperty] private int _ramUsage = 0;
    [ObservableProperty] private int _ssdUsage = 0;
    [ObservableProperty] private long _pingMs = 0;

    // Свойства для Проводника
    [ObservableProperty] private string _currentDirectory = "/root";
    [ObservableProperty] private ObservableCollection<RemoteFileItem> _files = new();

    // Строка ввода команды снизу
    [ObservableProperty] private string _commandInput = "";

    public VpnProfile? CurrentProfile { get; private set; }

    public TerminalViewModel() { }

    public void Initialize(VpnProfile profile, string command)
    {
        CurrentProfile = profile;
        WindowTitle = $"Терминал — {profile.IpAddress}";
        ConnectionInfo = $"ПОДКЛЮЧЕНО: {profile.Username}@{profile.IpAddress}";
    }

    // Этот метод мы вызовем из Code-Behind, когда передадим SSH сессию
    public async Task SetSshSessionAsync(ISshService ssh)
    {
        _sshService = ssh;
        await LoadFilesAsync(); // Загружаем файлы сразу после установки сессии
    }

    [RelayCommand]
    private async Task LoadFilesAsync()
    {
        if (_sshService == null || !_sshService.IsConnected) return;

        try
        {
            // ls -pA выводит файлы и папки (папки заканчиваются на слэш '/')
            string result = await _sshService.ExecuteCommandAsync($"ls -pA {CurrentDirectory}");
            var lines = result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                Files.Clear();

                // Добавляем папку "Назад", если мы не в корне
                if (CurrentDirectory != "/")
                {
                    Files.Add(new RemoteFileItem { Name = "..", IsDirectory = true });
                }

                foreach (var line in lines)
                {
                    bool isDir = line.EndsWith("/");
                    string name = isDir ? line.TrimEnd('/') : line;

                    // Игнорируем текущую директорию
                    if (name == "./" || name == "../" || name == ".") continue;

                    Files.Add(new RemoteFileItem { Name = name, IsDirectory = isDir });
                }
            });
        }
        catch { /* Игнорируем ошибки доступа к папкам */ }
    }

    [RelayCommand]
    private async Task NavigateAsync(RemoteFileItem? item)
    {
        if (item == null || !item.IsDirectory) return;

        if (item.Name == "..")
        {
            // Поднимаемся на уровень выше
            var parts = CurrentDirectory.TrimEnd('/').Split('/');
            if (parts.Length > 1)
            {
                CurrentDirectory = string.Join("/", parts.Take(parts.Length - 1));
                if (string.IsNullOrEmpty(CurrentDirectory)) CurrentDirectory = "/";
            }
        }
        else
        {
            // Проваливаемся внутрь папки
            CurrentDirectory = CurrentDirectory == "/" ? $"/{item.Name}" : $"{CurrentDirectory}/{item.Name}";
        }

        await LoadFilesAsync();
    }
}