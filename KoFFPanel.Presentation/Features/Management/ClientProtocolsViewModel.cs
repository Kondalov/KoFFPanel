using CommunityToolkit.Mvvm.ComponentModel;
using KoFFPanel.Application.Constants;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Runtime.Versioning;

namespace KoFFPanel.Presentation.Features.Management;

[SupportedOSPlatform("windows")]
public partial class ClientProtocolsViewModel : ObservableObject
{
    private readonly IProfileRepository _profileRepository;
    private VpnClient _originalClient = null!;

    [ObservableProperty] private string _windowTitle = "Управление протоколами";
    [ObservableProperty] private string _email = "";

    [ObservableProperty] private bool _isTrustTunnelMode = false;
    [ObservableProperty] private bool _supportsVless = true;
    [ObservableProperty] private bool _supportsHysteria2 = true;
    [ObservableProperty] private bool _supportsTrustTunnel = false;
    [ObservableProperty] private bool _supportsTrojan = false;
    [ObservableProperty] private bool _supportsShadowsocks = false;

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

    // === Trojan ===
    [ObservableProperty] private bool _isTrojanEnabled;
    [ObservableProperty] private string _trojanLink = "";
    [ObservableProperty] private bool _isTrojanCopied;

    // === Shadowsocks ===
    [ObservableProperty] private bool _isShadowsocksEnabled;
    [ObservableProperty] private string _shadowsocksLink = "";
    [ObservableProperty] private bool _isShadowsocksCopied;

    [ObservableProperty] private string _trustTunnelCertPath = "/opt/trusttunnel2/cert.pem";
    [ObservableProperty] private string _ttUsername = "";
    [ObservableProperty] private string _ttPassword = "";
    [ObservableProperty] private string _ttDomainName = "";
    [ObservableProperty] private string _ttDnsServers = "";
    [ObservableProperty] private string _ttServerAddress = "";
    [ObservableProperty] private bool _isAdmin;

    private readonly ISshService _ssh;
    private readonly IFilePickerService _filePicker;

    public Action<VpnClient>? SaveCallback { get; set; }
    public Action? CloseAction { get; set; }

    public ClientProtocolsViewModel(IProfileRepository profileRepository, ISshService ssh, IFilePickerService filePicker)
    {
        _profileRepository = profileRepository;
        _ssh = ssh;
        _filePicker = filePicker;
    }

    public void Initialize(VpnClient client, string httpLink)
    {
        _originalClient = client;
        Email = client.Email;
        WindowTitle = $"Протоколы: {client.Email}";
        IsAdmin = client.Email.Equals("ADMIN", StringComparison.OrdinalIgnoreCase);
        TtServerAddress = client.ServerIp;
        HttpLink = httpLink;

        var profile = _profileRepository.LoadProfiles().FirstOrDefault(p => p.IpAddress == client.ServerIp);
        profile?.MigrateLegacyData();
        var inbounds = profile?.Inbounds ?? new System.Collections.Generic.List<ServerInbound>();

        bool serverHasVless = inbounds.Any(i => i.Protocol.Equals("vless", StringComparison.OrdinalIgnoreCase));
        bool serverHasHysteria = inbounds.Any(i => i.Protocol.Equals("hysteria2", StringComparison.OrdinalIgnoreCase));
        bool serverHasTrustTunnel = inbounds.Any(i => i.Protocol.Equals("trusttunnel", StringComparison.OrdinalIgnoreCase));
        bool serverHasTrojan = inbounds.Any(i => i.Protocol.Equals("trojan", StringComparison.OrdinalIgnoreCase));
        bool serverHasShadowsocks = inbounds.Any(i => i.Protocol.Equals("shadowsocks", StringComparison.OrdinalIgnoreCase));

        SupportsVless = serverHasVless;
        SupportsHysteria2 = serverHasHysteria;
        SupportsTrustTunnel = serverHasTrustTunnel;
        SupportsTrojan = serverHasTrojan;
        SupportsShadowsocks = serverHasShadowsocks;

        IsTrustTunnelMode = serverHasTrustTunnel && !serverHasVless && !serverHasHysteria && !serverHasTrojan && !serverHasShadowsocks;

        IsVlessEnabled = client.IsVlessEnabled && serverHasVless;
        IsHysteria2Enabled = client.IsHysteria2Enabled && serverHasHysteria;
        IsTrustTunnelEnabled = client.IsTrustTunnelEnabled && serverHasTrustTunnel;
        IsTrojanEnabled = client.IsTrojanEnabled && serverHasTrojan;
        IsShadowsocksEnabled = client.IsShadowsocksEnabled && serverHasShadowsocks;

        VlessLink = client.VlessLink;
        Hysteria2Link = client.Hysteria2Link;
        TrustTunnelLink = client.TrustTunnelLink;
        TrojanLink = client.TrojanLink;
        ShadowsocksLink = client.ShadowsocksLink;

        if (serverHasVless && (string.IsNullOrWhiteSpace(VlessLink) || VlessLink.Contains("не установлен")))
            VlessLink = GenerateVlessLinkFallback(inbounds.First(i => i.Protocol.Equals("vless", StringComparison.OrdinalIgnoreCase)), client.ServerIp, client.Uuid, client.Email);

        if (serverHasHysteria && (string.IsNullOrWhiteSpace(Hysteria2Link) || Hysteria2Link.Contains("не установлен")))
            Hysteria2Link = GenerateHysteriaLinkFallback(inbounds.First(i => i.Protocol.Equals("hysteria2", StringComparison.OrdinalIgnoreCase)), client.ServerIp, client.Uuid, client.Email);

        if (serverHasTrustTunnel && (string.IsNullOrWhiteSpace(TrustTunnelLink) || TrustTunnelLink.Contains("не установлен")))
            TrustTunnelLink = GenerateTrustTunnelLinkFallback(inbounds.First(i => i.Protocol.Equals("trusttunnel", StringComparison.OrdinalIgnoreCase)), client.ServerIp, client.Uuid, client.Email);

        TtUsername = client.Email;
        TtPassword = client.Uuid;

        if (serverHasTrustTunnel)
        {
            ExtractTrustTunnelSettingsSafe(inbounds);
            var ttInbound = inbounds.FirstOrDefault(i => i.Protocol.Equals("trusttunnel", StringComparison.OrdinalIgnoreCase));
            if (ttInbound != null && !string.IsNullOrWhiteSpace(ttInbound.SettingsJson))
            {
                try
                {
                    var ttSettings = System.Text.Json.JsonDocument.Parse(ttInbound.SettingsJson).RootElement;
                    if (ttSettings.TryGetProperty("username", out var u)) TtUsername = u.GetString() ?? TtUsername;
                    if (ttSettings.TryGetProperty("password", out var p)) TtPassword = p.GetString() ?? TtPassword;
                }
                catch { }
            }
        }
        else SetDefaultTrustTunnelSettings();
    }

    private string GenerateVlessLinkFallback(ServerInbound inbound, string ip, string uuid, string email)
    {
        try
        {
            var settings = System.Text.Json.JsonDocument.Parse(inbound.SettingsJson).RootElement;
            string pub = settings.GetProperty("publicKey").GetString() ?? "";
            string sni = settings.GetProperty("sni").GetString() ?? "google.com";
            string sid = settings.GetProperty("shortId").GetString() ?? "";
            string safeIp = ip.Contains(":") && !ip.StartsWith("[") ? $"[{ip}]" : ip;
            return $"vless://{uuid}@{safeIp}:{inbound.Port}?type=tcp&security=reality&pbk={pub}&fp=chrome&sni={sni}&sid={sid}&spx=%2F&flow=xtls-rprx-vision&alpn=h2#KoFF_{email}";
        }
        catch { return "Ошибка генерации ссылки"; }
    }

    private string GenerateHysteriaLinkFallback(ServerInbound inbound, string ip, string uuid, string email)
    {
        try
        {
            var settings = System.Text.Json.JsonDocument.Parse(inbound.SettingsJson).RootElement;
            string sni = settings.GetProperty("sni").GetString() ?? "bing.com";
            string obfs = settings.GetProperty("obfsPassword").GetString() ?? "";
            string safeIp = ip.Contains(":") && !ip.StartsWith("[") ? $"[{ip}]" : ip;
            string encodedName = Uri.EscapeDataString($"KoFF_{email}");
            return $"hy2://{uuid}@{safeIp}:{inbound.Port}?sni={sni}&obfs=salamander&obfs-password={obfs}&insecure=1#{encodedName}";
        }
        catch { return "Ошибка генерации ссылки"; }
    }

    private string GenerateTrustTunnelLinkFallback(ServerInbound inbound, string ip, string uuid, string email)
    {
        string safeIp = ip.Contains(":") && !ip.StartsWith("[") ? $"[{ip}]" : ip;
        return $"vless://{uuid}@{safeIp}:{inbound.Port}?type=xhttp&security=tls&sni=google.com&alpn=h3#TT_{email}";
    }

    private void ExtractTrustTunnelSettingsSafe(System.Collections.Generic.IEnumerable<ServerInbound> inbounds)
    {
        var ttInbound = inbounds.FirstOrDefault(i => i.Protocol.Equals("trusttunnel", StringComparison.OrdinalIgnoreCase));
        if (ttInbound is null || string.IsNullOrWhiteSpace(ttInbound.SettingsJson))
        {
            SetDefaultTrustTunnelSettings();
            return;
        }

        try
        {
            var ttSettings = System.Text.Json.JsonDocument.Parse(ttInbound.SettingsJson).RootElement;
            TtDomainName = ttSettings.GetProperty("sni").GetString() ?? "google.com";
            TtDnsServers = "8.8.8.8, 1.1.1.1";
        }
        catch (System.Text.Json.JsonException)
        {
            SetDefaultTrustTunnelSettings();
        }
    }

    private void SetDefaultTrustTunnelSettings()
    {
        TtDomainName = "google.com";
        TtDnsServers = "8.8.8.8, 1.1.1.1";
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

    [RelayCommand] private async Task CopyTtPasswordAsync() { if (string.IsNullOrWhiteSpace(TtPassword)) return; await SafeCopyToClipboardAsync(TtPassword); IsTrustTunnelCopied = true; await Task.Delay(2000); IsTrustTunnelCopied = false; }
    [RelayCommand] private async Task CopyHttpAsync() { if (string.IsNullOrWhiteSpace(HttpLink)) return; await SafeCopyToClipboardAsync(HttpLink); IsHttpCopied = true; await Task.Delay(2000); IsHttpCopied = false; }
    [RelayCommand] private async Task CopyVlessAsync() { if (string.IsNullOrWhiteSpace(VlessLink)) return; await SafeCopyToClipboardAsync(VlessLink); IsVlessCopied = true; await Task.Delay(2000); IsVlessCopied = false; }
    [RelayCommand] private async Task CopyHysteria2Async() { if (string.IsNullOrWhiteSpace(Hysteria2Link)) return; await SafeCopyToClipboardAsync(Hysteria2Link); IsHysteria2Copied = true; await Task.Delay(2000); IsHysteria2Copied = false; }
    [RelayCommand] private async Task CopyTrustTunnelAsync() { if (string.IsNullOrWhiteSpace(TrustTunnelLink)) return; await SafeCopyToClipboardAsync(TrustTunnelLink); IsTrustTunnelCopied = true; await Task.Delay(2000); IsTrustTunnelCopied = false; }

    [RelayCommand] private async Task CopyTrojanAsync() { if (string.IsNullOrWhiteSpace(TrojanLink)) return; await SafeCopyToClipboardAsync(TrojanLink); IsTrojanCopied = true; await Task.Delay(2000); IsTrojanCopied = false; }
    [RelayCommand] private async Task CopyShadowsocksAsync() { if (string.IsNullOrWhiteSpace(ShadowsocksLink)) return; await SafeCopyToClipboardAsync(ShadowsocksLink); IsShadowsocksCopied = true; await Task.Delay(2000); IsShadowsocksCopied = false; }

    [RelayCommand]
    private async Task DownloadCertAsync()
    {
        if (!IsAdmin || !_ssh.IsConnected) return;
        try
        {
            string? localPath = _filePicker.SaveFile("cert.pem", "PEM Certificate (*.pem)|*.pem|All files (*.*)|*.*");
            if (string.IsNullOrEmpty(localPath)) return;
            using (var localStream = System.IO.File.Create(localPath))
            {
                await _ssh.DownloadFileAsync(TrustTunnelCertPath, localStream);
            }
            MessageBox.Show($"Файл успешно сохранен: {localPath}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при скачивании: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void Save()
    {
        _originalClient.IsVlessEnabled = IsVlessEnabled;
        _originalClient.IsHysteria2Enabled = IsHysteria2Enabled;
        _originalClient.IsTrustTunnelEnabled = IsTrustTunnelEnabled;
        _originalClient.IsTrojanEnabled = IsTrojanEnabled;
        _originalClient.IsShadowsocksEnabled = IsShadowsocksEnabled;

        SaveCallback?.Invoke(_originalClient);
        CloseAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseAction?.Invoke();
}