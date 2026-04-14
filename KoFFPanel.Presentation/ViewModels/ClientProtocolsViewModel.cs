using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Domain.Entities;
using System;
using System.Threading.Tasks;
using System.Windows;

namespace KoFFPanel.Presentation.ViewModels;

public partial class ClientProtocolsViewModel : ObservableObject
{
    private VpnClient _originalClient = null!;

    [ObservableProperty] private string _windowTitle = "Управление протоколами";
    [ObservableProperty] private string _email = "";

    // === VLESS ===
    [ObservableProperty] private bool _isVlessEnabled;
    [ObservableProperty] private string _vlessLink = "";
    [ObservableProperty] private bool _isVlessCopied;

    // === Hysteria 2 ===
    [ObservableProperty] private bool _isHysteria2Enabled;
    [ObservableProperty] private string _hysteria2Link = "";
    [ObservableProperty] private bool _isHysteria2Copied;

    // === TrustTunnel ===
    [ObservableProperty] private bool _isTrustTunnelEnabled;
    [ObservableProperty] private string _trustTunnelLink = "";
    [ObservableProperty] private bool _isTrustTunnelCopied;

    public Action<VpnClient>? SaveCallback { get; set; }
    public Action? CloseAction { get; set; }

    public void Initialize(VpnClient client)
    {
        _originalClient = client;
        Email = client.Email;
        WindowTitle = $"Протоколы: {client.Email}";

        IsVlessEnabled = client.IsVlessEnabled;
        VlessLink = client.VlessLink;

        IsHysteria2Enabled = client.IsHysteria2Enabled;
        Hysteria2Link = client.Hysteria2Link;

        IsTrustTunnelEnabled = client.IsTrustTunnelEnabled;
        TrustTunnelLink = client.TrustTunnelLink;
    }

    // ИСПРАВЛЕНИЕ: Жесткая защита буфера обмена от блокировок WPF (COMException)
    private async Task SafeCopyToClipboardAsync(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetText(text);
                return; // Успешно скопировано
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                await Task.Delay(20); // Ждем 20 мс и агрессивно пробуем снова
            }
        }
    }

    [RelayCommand]
    private async Task CopyVlessAsync()
    {
        if (string.IsNullOrWhiteSpace(VlessLink)) return;
        await SafeCopyToClipboardAsync(VlessLink);
        IsVlessCopied = true;
        await Task.Delay(2000);
        IsVlessCopied = false;
    }

    [RelayCommand]
    private async Task CopyHysteria2Async()
    {
        if (string.IsNullOrWhiteSpace(Hysteria2Link)) return;
        await SafeCopyToClipboardAsync(Hysteria2Link);
        IsHysteria2Copied = true;
        await Task.Delay(2000);
        IsHysteria2Copied = false;
    }

    [RelayCommand]
    private async Task CopyTrustTunnelAsync()
    {
        if (string.IsNullOrWhiteSpace(TrustTunnelLink)) return;
        await SafeCopyToClipboardAsync(TrustTunnelLink);
        IsTrustTunnelCopied = true;
        await Task.Delay(2000);
        IsTrustTunnelCopied = false;
    }

    [RelayCommand]
    private void Save()
    {
        _originalClient.IsVlessEnabled = IsVlessEnabled;
        _originalClient.IsHysteria2Enabled = IsHysteria2Enabled;
        _originalClient.IsTrustTunnelEnabled = IsTrustTunnelEnabled;

        SaveCallback?.Invoke(_originalClient);
        CloseAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        CloseAction?.Invoke();
    }
}