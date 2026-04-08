using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.ViewModels;

public partial class AddServerViewModel : ObservableObject
{
    private readonly IProfileRepository _profileRepository;
    private readonly ISshService _sshService;
    private readonly IFilePickerService _filePickerService;

    public Action? CloseAction { get; set; }

    [ObservableProperty] private string _windowTitle = "Добавление сервера";
    public bool IsEditMode { get; private set; } = false;
    private string _editingServerId = string.Empty;

    [ObservableProperty] private string _name = "Новый сервер";
    [ObservableProperty] private string _ipAddress = "";
    [ObservableProperty] private int _port = 22;
    [ObservableProperty] private string _username = "root";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _keyPath = "";

    [ObservableProperty] private string _statusMessage = "";

    [ObservableProperty] private bool _isChecking = false;
    [ObservableProperty] private bool _isNotChecking = true;

    // Инжектим новый сервис выбора файлов
    public AddServerViewModel(
        IProfileRepository profileRepository,
        ISshService sshService,
        IFilePickerService filePickerService)
    {
        _profileRepository = profileRepository;
        _sshService = sshService;
        _filePickerService = filePickerService;
    }

    // МЕТОД ДЛЯ ЗАГРУЗКИ ДАННЫХ ПРИ РЕДАКТИРОВАНИИ
    public void LoadForEdit(VpnProfile profile)
    {
        IsEditMode = true;
        WindowTitle = "Редактирование сервера";
        _editingServerId = profile.Id;

        Name = profile.Name;
        IpAddress = profile.IpAddress;
        Port = profile.Port;
        Username = profile.Username;
        Password = profile.Password;
        KeyPath = profile.KeyPath;
    }

    [RelayCommand]
    private void BrowseKey()
    {
        var path = _filePickerService.PickSshKeyFile();
        if (!string.IsNullOrEmpty(path))
        {
            KeyPath = path;
        }
    }

    [RelayCommand]
    private async Task CheckConnectionAsync()
    {
        if (string.IsNullOrWhiteSpace(IpAddress))
        {
            StatusMessage = "Введите IP-адрес!";
            return;
        }

        IsChecking = true;
        IsNotChecking = false;
        StatusMessage = "Проверка подключения (до 15 сек)...";

        string result = await _sshService.ConnectAsync(IpAddress, Port, Username, Password, KeyPath);

        if (result == "SUCCESS")
        {
            StatusMessage = "✅ Успешно! Сервер доступен.";
            _sshService.Disconnect();
        }
        else
        {
            StatusMessage = $"❌ Ошибка: {result}";
        }

        IsChecking = false;
        IsNotChecking = true;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(IpAddress) || string.IsNullOrWhiteSpace(Name))
        {
            StatusMessage = "Заполните Название и IP-адрес!";
            return;
        }

        // Бронебойное создание профиля
        var profileToSave = new VpnProfile
        {
            // Если редактируем - сохраняем старый ID, иначе генерируем новый
            Id = IsEditMode ? _editingServerId : Guid.NewGuid().ToString(),
            Name = Name,
            IpAddress = IpAddress,
            Port = Port <= 0 ? 22 : Port, // Защита от нулевого порта
            Username = string.IsNullOrWhiteSpace(Username) ? "root" : Username,
            Password = Password ?? string.Empty, // Защита от null
            KeyPath = KeyPath ?? string.Empty    // Защита от null
        };

        if (IsEditMode)
        {
            _profileRepository.UpdateProfile(profileToSave);
        }
        else
        {
            _profileRepository.AddProfile(profileToSave);
        }

        CloseAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseAction?.Invoke();
    }
}