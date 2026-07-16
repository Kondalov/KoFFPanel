using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Runtime.Versioning;

namespace KoFFPanel.Presentation.Features.Management;

[SupportedOSPlatform("windows")]
public partial class AddClientViewModel : ObservableObject
{
    [ObservableProperty] private string _clientName = "";
    [ObservableProperty] private int _trafficLimitGb = 0;
    [ObservableProperty] private DateTime? _expiryDate = null;
    [ObservableProperty] private string _note = "";

    // Флаги протоколов (СТАРЫЕ - НЕ ТРОГАЕМ)
    [ObservableProperty] private bool _isVlessEnabled = true;
    [ObservableProperty] private bool _isHysteria2Enabled = true;
    [ObservableProperty] private bool _isTrustTunnelEnabled = true;

    // НОВЫЕ СВОЙСТВА
    [ObservableProperty] private bool _isTrojanEnabled = false;
    [ObservableProperty] private bool _isShadowsocksEnabled = false;

    [ObservableProperty] private bool _isP2PBlocked = true;
    [ObservableProperty] private bool _areProtocolsExpanded = false;

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
        AreProtocolsExpanded = false;

        IsVlessEnabled = true;
        IsHysteria2Enabled = true;
        IsTrustTunnelEnabled = true;
        IsTrojanEnabled = false;
        IsShadowsocksEnabled = false;
    }

    public void LoadForEdit(string currentName, long currentLimitBytes, DateTime? currentExpiry, string currentNote,
                           bool isP2pBlocked = true, bool isVless = true, bool isHy2 = false, bool isTt = false,
                           bool isTrojan = false, bool isShadowsocks = false)
    {
        IsEditMode = true;
        WindowTitle = "Редактировать пользователя";
        ActionButtonText = "Сохранить";
        AreProtocolsExpanded = false;

        ClientName = currentName;
        TrafficLimitGb = (int)(currentLimitBytes / 1024 / 1024 / 1024);
        ExpiryDate = currentExpiry;
        Note = currentNote ?? "";
        IsP2PBlocked = isP2pBlocked;

        IsVlessEnabled = isVless;
        IsHysteria2Enabled = isHy2;
        IsTrustTunnelEnabled = isTt;
        IsTrojanEnabled = isTrojan;
        IsShadowsocksEnabled = isShadowsocks;
    }

    [RelayCommand]
    private void ToggleProtocols()
    {
        AreProtocolsExpanded = !AreProtocolsExpanded;
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