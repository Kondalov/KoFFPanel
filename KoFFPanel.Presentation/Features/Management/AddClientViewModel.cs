using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace KoFFPanel.Presentation.Features.Management;

public partial class AddClientViewModel : ObservableObject
{
    [ObservableProperty] private string _clientName = "";
    [ObservableProperty] private int _trafficLimitGb = 0;
    [ObservableProperty] private DateTime? _expiryDate = null;
    [ObservableProperty] private string _note = "";

    // Флаги протоколов
    [ObservableProperty] private bool _isVlessEnabled = true;
    [ObservableProperty] private bool _isHysteria2Enabled = true;
    [ObservableProperty] private bool _isTrustTunnelEnabled = true;

    // НОВОЕ СВОЙСТВО: Флаг блокировки торрентов (По умолчанию включено для безопасности сервера)
    [ObservableProperty] private bool _isP2PBlocked = true;

    [ObservableProperty] private string _windowTitle = "Добавить пользователя";
    [ObservableProperty] private string _actionButtonText = "Создать";

    public bool IsEditMode { get; private set; } = false;
    public bool IsSuccess { get; private set; } = false;
    public Action? CloseAction { get; set; }

    public void Initialize(string serverIp)
    {
        IsEditMode = false;
        WindowTitle = "Добавить пользователя";
        ActionButtonText = "Создать";
        ClientName = "";
        TrafficLimitGb = 0;
        ExpiryDate = DateTime.Now.AddMonths(1);
        Note = "";
        IsP2PBlocked = true;
        
        IsVlessEnabled = true;
        IsHysteria2Enabled = true;
        IsTrustTunnelEnabled = true;
    }

    // Обновленный метод загрузки с учетом P2P флага и протоколов
    public void LoadForEdit(string currentName, long currentLimitBytes, DateTime? currentExpiry, string currentNote, 
                           bool isP2pBlocked = true, bool isVless = true, bool isHy2 = false, bool isTt = false)
    {
        IsEditMode = true;
        WindowTitle = "Редактировать пользователя";
        ActionButtonText = "Сохранить";

        ClientName = currentName;
        TrafficLimitGb = (int)(currentLimitBytes / 1024 / 1024 / 1024);
        ExpiryDate = currentExpiry;
        Note = currentNote ?? "";
        IsP2PBlocked = isP2pBlocked;
        
        IsVlessEnabled = isVless;
        IsHysteria2Enabled = isHy2;
        IsTrustTunnelEnabled = isTt;
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(ClientName)) return;

        IsSuccess = true;
        CloseAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsSuccess = false;
        CloseAction?.Invoke();
    }
}