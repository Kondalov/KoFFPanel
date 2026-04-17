using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace KoFFPanel.Presentation.ViewModels;

public partial class ClientProtocolsViewModel : ObservableObject
{
    private readonly IProfileRepository _profileRepository;
    private VpnClient _originalClient = null!;

    [ObservableProperty] private string _windowTitle = "Управление протоколами";
    [ObservableProperty] private string _email = "";

    [ObservableProperty] private bool _isTrustTunnelMode = false;

    // === HTTP ПОДПИСКА ===
    [ObservableProperty] private string _httpLink = "";
    [ObservableProperty] private bool _isHttpCopied;

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

    public ClientProtocolsViewModel(IProfileRepository profileRepository)
    {
        _profileRepository = profileRepository;
    }

    // ИСПРАВЛЕНИЕ: Добавлен параметр httpLink из CabinetViewModel
    public void Initialize(VpnClient client, string httpLink)
    {
        _originalClient = client;
        Email = client.Email;
        WindowTitle = $"Протоколы: {client.Email}";

        HttpLink = httpLink;

        var profile = _profileRepository.LoadProfiles().FirstOrDefault(p => p.IpAddress == client.ServerIp);
        IsTrustTunnelMode = (profile?.CoreType?.ToLower() == "trusttunnel");

        IsVlessEnabled = client.IsVlessEnabled;
        VlessLink = client.VlessLink;

        IsHysteria2Enabled = client.IsHysteria2Enabled;
        Hysteria2Link = client.Hysteria2Link;

        IsTrustTunnelEnabled = client.IsTrustTunnelEnabled;
        TrustTunnelLink = client.TrustTunnelLink;
    }

    private async Task SafeCopyToClipboardAsync(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try
            {
                System.Windows.Application.Current.Dispatcher.Invoke(() => Clipboard.SetText(text));
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                await Task.Delay(20);
            }
        }
    }

    [RelayCommand]
    private async Task CopyHttpAsync()
    {
        if (string.IsNullOrWhiteSpace(HttpLink)) return;
        await SafeCopyToClipboardAsync(HttpLink);
        IsHttpCopied = true;
        await Task.Delay(2000);
        IsHttpCopied = false;
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