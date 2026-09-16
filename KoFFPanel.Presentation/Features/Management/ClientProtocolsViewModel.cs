using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using System.Windows;

namespace KoFFPanel.Presentation.Features.Management;

[SupportedOSPlatform("windows")]
public partial class ClientProtocolsViewModel : ObservableObject
{
    private readonly IProfileRepository _profileRepository;
    private VpnClient _originalClient = null!;

    [ObservableProperty] private string _windowTitle = "Управление протоколами";
    [ObservableProperty] private string _email = "";

    [ObservableProperty] private bool _supportsVless = true;
    [ObservableProperty] private bool _supportsHysteria2 = true;
    [ObservableProperty] private bool _supportsTuic = false;
    [ObservableProperty] private bool _supportsTrojan = false;

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

    // === TUIC v5 ===
    [ObservableProperty] private bool _isTuicEnabled;
    [ObservableProperty] private string _tuicLink = "";
    [ObservableProperty] private bool _isTuicCopied;

    // === Trojan ===
    [ObservableProperty] private bool _isTrojanEnabled;
    [ObservableProperty] private string _trojanLink = "";
    [ObservableProperty] private bool _isTrojanCopied;

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
        HttpLink = httpLink;

        var profile = _profileRepository.LoadProfiles().FirstOrDefault(p => p.IpAddress == client.ServerIp);
        profile?.MigrateLegacyData();
        var inbounds = profile?.Inbounds ?? new System.Collections.Generic.List<ServerInbound>();

        bool serverHasVless = inbounds.Any(i => i.Protocol.Equals("vless", StringComparison.OrdinalIgnoreCase));
        bool serverHasHysteria = inbounds.Any(i => i.Protocol.Equals("hysteria2", StringComparison.OrdinalIgnoreCase));
        bool serverHasTuic = inbounds.Any(i => i.Protocol.Equals("tuic", StringComparison.OrdinalIgnoreCase));
        bool serverHasTrojan = inbounds.Any(i => i.Protocol.Equals("trojan", StringComparison.OrdinalIgnoreCase));

        SupportsVless = serverHasVless;
        SupportsHysteria2 = serverHasHysteria;
        SupportsTuic = serverHasTuic;
        SupportsTrojan = serverHasTrojan;

        IsVlessEnabled = client.IsVlessEnabled && serverHasVless;
        IsHysteria2Enabled = client.IsHysteria2Enabled && serverHasHysteria;
        IsTuicEnabled = client.IsTuicEnabled && serverHasTuic;
        IsTrojanEnabled = client.IsTrojanEnabled && serverHasTrojan;

        VlessLink = client.VlessLink;
        Hysteria2Link = client.Hysteria2Link;
        TuicLink = client.TuicLink;
        TrojanLink = client.TrojanLink;

        if (serverHasVless && (string.IsNullOrWhiteSpace(VlessLink) || VlessLink.Contains("не установлен")))
            VlessLink = GenerateVlessLinkFallback(inbounds.First(i => i.Protocol.Equals("vless", StringComparison.OrdinalIgnoreCase)), client.ServerIp, client.Uuid, client.Email);

        if (serverHasHysteria && (string.IsNullOrWhiteSpace(Hysteria2Link) || Hysteria2Link.Contains("не установлен")))
            Hysteria2Link = GenerateHysteriaLinkFallback(inbounds.First(i => i.Protocol.Equals("hysteria2", StringComparison.OrdinalIgnoreCase)), client.ServerIp, client.Uuid, client.Email);

        if (serverHasTuic && (string.IsNullOrWhiteSpace(TuicLink) || TuicLink.Contains("не установлен")))
            TuicLink = GenerateTuicLinkFallback(inbounds.First(i => i.Protocol.Equals("tuic", StringComparison.OrdinalIgnoreCase)), client.ServerIp, client.Uuid, client.Email);
    }

    private string GenerateVlessLinkFallback(ServerInbound inbound, string ip, string uuid, string email)
    {
        try
        {
            var settings = System.Text.Json.JsonDocument.Parse(inbound.SettingsJson).RootElement;
            string pub = settings.GetProperty("publicKey").GetString() ?? "";
            string sni = settings.GetProperty("sni").GetString() ?? "dl.google.com";
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

    private string GenerateTuicLinkFallback(ServerInbound inbound, string ip, string uuid, string email)
    {
        try
        {
            string sni = "bing.com";
            if (!string.IsNullOrWhiteSpace(inbound.SettingsJson))
            {
                var settings = System.Text.Json.JsonDocument.Parse(inbound.SettingsJson).RootElement;
                if (settings.TryGetProperty("sni", out var s) && !string.IsNullOrWhiteSpace(s.GetString()))
                    sni = s.GetString()!;
            }
            string safeIp = ip.Contains(":") && !ip.StartsWith("[") ? $"[{ip}]" : ip;
            string encodedName = Uri.EscapeDataString($"KoFF_TUIC_{email}");
            return $"tuic://{uuid}:{uuid}@{safeIp}:{inbound.Port}?sni={sni}&alpn=h3&congestion_control=bbr&allow_insecure=1#{encodedName}";
        }
        catch { return "Ошибка генерации ссылки"; }
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

    [RelayCommand] private async Task CopyHttpAsync() { if (string.IsNullOrWhiteSpace(HttpLink)) return; await SafeCopyToClipboardAsync(HttpLink); IsHttpCopied = true; await Task.Delay(2000); IsHttpCopied = false; }
    [RelayCommand] private async Task CopyVlessAsync() { if (string.IsNullOrWhiteSpace(VlessLink)) return; await SafeCopyToClipboardAsync(VlessLink); IsVlessCopied = true; await Task.Delay(2000); IsVlessCopied = false; }
    [RelayCommand] private async Task CopyHysteria2Async() { if (string.IsNullOrWhiteSpace(Hysteria2Link)) return; await SafeCopyToClipboardAsync(Hysteria2Link); IsHysteria2Copied = true; await Task.Delay(2000); IsHysteria2Copied = false; }
    [RelayCommand] private async Task CopyTuicAsync() { if (string.IsNullOrWhiteSpace(TuicLink)) return; await SafeCopyToClipboardAsync(TuicLink); IsTuicCopied = true; await Task.Delay(2000); IsTuicCopied = false; }
    [RelayCommand] private async Task CopyTrojanAsync() { if (string.IsNullOrWhiteSpace(TrojanLink)) return; await SafeCopyToClipboardAsync(TrojanLink); IsTrojanCopied = true; await Task.Delay(2000); IsTrojanCopied = false; }

    [RelayCommand]
    private void Save()
    {
        _originalClient.IsVlessEnabled = IsVlessEnabled;
        _originalClient.IsHysteria2Enabled = IsHysteria2Enabled;
        _originalClient.IsTuicEnabled = IsTuicEnabled;
        _originalClient.IsTrojanEnabled = IsTrojanEnabled;

        SaveCallback?.Invoke(_originalClient);
        CloseAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel() => CloseAction?.Invoke();
}