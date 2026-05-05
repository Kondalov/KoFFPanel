using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using MaxMind.GeoIP2;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

using System.Runtime.Versioning;

namespace KoFFPanel.Presentation.Features.Management;

[SupportedOSPlatform("windows")]
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

    [ObservableProperty] private string _customDomain = "";
    [ObservableProperty] private string _connectionNode = "";

    [ObservableProperty] private string _statusMessage = "";

    [ObservableProperty] private bool _isChecking = false;
    [ObservableProperty] private bool _isNotChecking = true;

    public AddServerViewModel(
        IProfileRepository profileRepository,
        ISshService sshService,
        IFilePickerService filePickerService)
    {
        _profileRepository = profileRepository;
        _sshService = sshService;
        _filePickerService = filePickerService;
    }

    public void LoadForEdit(VpnProfile profile)
    {
        IsEditMode = true;
        WindowTitle = "Редактирование сервера";
        _editingServerId = profile.Id ?? string.Empty;

        Name = profile.Name ?? "Новый сервер";
        IpAddress = profile.IpAddress ?? string.Empty;
        Port = profile.Port;
        Username = profile.Username ?? "root";
        Password = profile.Password ?? string.Empty;
        KeyPath = profile.KeyPath ?? string.Empty;
        CustomDomain = profile.CustomDomain ?? string.Empty;
        ConnectionNode = profile.ConnectionNode ?? string.Empty;
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
    private void ClearKey()
    {
        KeyPath = string.Empty;
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

        string cleanIp = IpAddress.Trim();
        VpnProfile profileToSave;

        if (IsEditMode)
        {
            var existingProfile = _profileRepository.LoadProfiles().FirstOrDefault(p => p.Id == _editingServerId);
            profileToSave = existingProfile ?? new VpnProfile { Id = _editingServerId };
            
            profileToSave.Name = Name;
            profileToSave.IpAddress = cleanIp;
            profileToSave.Port = Port <= 0 ? 22 : Port;
            profileToSave.Username = string.IsNullOrWhiteSpace(Username) ? "root" : Username;
            profileToSave.Password = Password ?? string.Empty;
            profileToSave.KeyPath = KeyPath ?? string.Empty;
            profileToSave.CustomDomain = CustomDomain?.Trim() ?? string.Empty;
            profileToSave.ConnectionNode = ConnectionNode?.Trim() ?? string.Empty;
            
            _profileRepository.UpdateProfile(profileToSave);
        }
        else
        {
            profileToSave = new VpnProfile
            {
                Id = Guid.NewGuid().ToString(),
                Name = Name,
                IpAddress = cleanIp,
                Port = Port <= 0 ? 22 : Port,
                Username = string.IsNullOrWhiteSpace(Username) ? "root" : Username,
                Password = Password ?? string.Empty,
                KeyPath = KeyPath ?? string.Empty,
                CustomDomain = CustomDomain?.Trim() ?? string.Empty,
                ConnectionNode = ConnectionNode?.Trim() ?? string.Empty
            };
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
