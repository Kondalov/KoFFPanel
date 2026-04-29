using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Text.RegularExpressions;

namespace KoFFPanel.Presentation.Features.Management;

public partial class AddClientViewModel : ObservableObject
{
    [ObservableProperty] private string _clientName = "";
    [ObservableProperty] private int _trafficLimitGb = 0;
    [ObservableProperty] private DateTime? _expiryDate = null;
    [ObservableProperty] private string _note = "";

    // Внутренние флаги (скрыты из UI для упрощения)
    public bool IsVlessEnabled { get; set; } = true;
    public bool IsHysteria2Enabled { get; set; } = true;
    public bool IsTrustTunnelEnabled { get; set; } = true;
    public bool IsP2PBlocked { get; set; } = true;

    [ObservableProperty] private string _windowTitle = "Добавить пользователя";
    [ObservableProperty] private string _actionButtonText = "Создать";
    [ObservableProperty] private string _statusMessage = "";

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
        
        StatusMessage = "";
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
        
        StatusMessage = "";
    }

    [RelayCommand]
    private void Save()
    {
        StatusMessage = "";
        // === SMART VALIDATION (PROTECTION FROM ERRORS) ===
        if (string.IsNullOrWhiteSpace(ClientName))
        {
            StatusMessage = "❌ Имя пользователя обязательно!";
            return;
        }

        // Регулярное выражение: только латиница, цифры, подчеркивания и тире
        if (!Regex.IsMatch(ClientName, "^[a-zA-Z0-9_-]+$"))
        {
            StatusMessage = "❌ Только латиница и цифры!";
            return;
        }

        if (TrafficLimitGb < 0)
        {
            StatusMessage = "❌ Лимит не может быть отрицательным!";
            return;
        }

        if (ExpiryDate.HasValue && ExpiryDate.Value < DateTime.Today && !IsEditMode)
        {
            StatusMessage = "❌ Дата истечения не может быть в прошлом!";
            return;
        }

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