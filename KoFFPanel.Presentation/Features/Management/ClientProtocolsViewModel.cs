using CommunityToolkit.Mvvm.ComponentModel;
using KoFFPanel.Application.Constants;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace KoFFPanel.Presentation.Features.Management;

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
    [ObservableProperty] private string _trustTunnelCertPath = "/opt/trusttunnel/cert.pem";
    [ObservableProperty] private string _ttUsername = "";
    [ObservableProperty] private string _ttPassword = "";
    [ObservableProperty] private string _ttDomainName = "";
    [ObservableProperty] private string _ttDnsServers = "";
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

    // ИСПРАВЛЕНИЕ: Добавлен параметр httpLink из CabinetViewModel
    public void Initialize(VpnClient client, string httpLink)
    {
        _originalClient = client;
        Email = client.Email;
        WindowTitle = $"Протоколы: {client.Email}";
        IsAdmin = client.Email.Equals("ADMIN", StringComparison.OrdinalIgnoreCase);

        HttpLink = httpLink;

        var profile = _profileRepository.LoadProfiles().FirstOrDefault(p => p.IpAddress == client.ServerIp);

        // Если профиль не найден по IP (дубли или смена IP), пробуем найти по ID, если он есть у клиента
        // (Допустим, мы добавим ServerId в VpnClient позже, пока полагаемся на IP)

        profile?.MigrateLegacyData();

        var inbounds = profile?.Inbounds ?? new List<ServerInbound>();

        // УМНЫЙ АЛГОРИТМ ОПРЕДЕЛЕНИЯ ПОДДЕРЖКИ
        bool serverHasVless = inbounds.Any(i => i.Protocol.Equals("vless", StringComparison.OrdinalIgnoreCase));
        bool serverHasHysteria = inbounds.Any(i => i.Protocol.Equals("hysteria2", StringComparison.OrdinalIgnoreCase));
        bool serverHasTrustTunnel = inbounds.Any(i => i.Protocol.Equals("trusttunnel", StringComparison.OrdinalIgnoreCase));

        SupportsVless = serverHasVless;
        SupportsHysteria2 = serverHasHysteria;
        SupportsTrustTunnel = serverHasTrustTunnel;

        // РЕЖИМ ОТОБРАЖЕНИЯ:
        // Если на сервере установлен ТОЛЬКО TrustTunnel - это спец-режим.
        // Во всех остальных случаях (Xray, Sing-Box) показываем стандартный вид с подпиской.
        IsTrustTunnelMode = serverHasTrustTunnel && !serverHasVless && !serverHasHysteria;

        // СИНХРОНИЗАЦИЯ ВКЛЮЧЕНИЯ (UI должен соответствовать реальности сервера)
        IsVlessEnabled = client.IsVlessEnabled && serverHasVless;
        IsHysteria2Enabled = client.IsHysteria2Enabled && serverHasHysteria;
        
        // TrustTunnel включаем только если он есть на сервере
        IsTrustTunnelEnabled = client.IsTrustTunnelEnabled && serverHasTrustTunnel;

        // ГЕНЕРАЦИЯ ССЫЛОК НА ЛЕТУ (Если в БД пусто, но сервер поддерживает)
        VlessLink = client.VlessLink;
        if (serverHasVless && (string.IsNullOrWhiteSpace(VlessLink) || VlessLink.Contains("не установлен")))
        {
            var vlessInbound = inbounds.First(i => i.Protocol.Equals("vless", StringComparison.OrdinalIgnoreCase));
            VlessLink = GenerateVlessLinkFallback(vlessInbound, client.ServerIp, client.Uuid, client.Email);
        }

        Hysteria2Link = client.Hysteria2Link;
        if (serverHasHysteria && (string.IsNullOrWhiteSpace(Hysteria2Link) || Hysteria2Link.Contains("не установлен")))
        {
            var hy2Inbound = inbounds.First(i => i.Protocol.Equals("hysteria2", StringComparison.OrdinalIgnoreCase));
            Hysteria2Link = GenerateHysteriaLinkFallback(hy2Inbound, client.ServerIp, client.Uuid, client.Email);
        }

        TrustTunnelLink = client.TrustTunnelLink;
        if (serverHasTrustTunnel && (string.IsNullOrWhiteSpace(TrustTunnelLink) || TrustTunnelLink.Contains("не установлен")))
        {
            var ttInbound = inbounds.First(i => i.Protocol.Equals("trusttunnel", StringComparison.OrdinalIgnoreCase));
            TrustTunnelLink = GenerateTrustTunnelLinkFallback(ttInbound, client.ServerIp, client.Uuid, client.Email);
        }

        TtUsername = client.Email;
        TtPassword = client.Uuid;

        if (serverHasTrustTunnel) ExtractTrustTunnelSettingsSafe(inbounds);
        else SetDefaultTrustTunnelSettings();
    }

    private string GenerateVlessLinkFallback(ServerInbound inbound, string ip, string uuid, string email)
    {
        try {
            var settings = System.Text.Json.JsonDocument.Parse(inbound.SettingsJson).RootElement;
            string pub = settings.GetProperty("publicKey").GetString() ?? "";
            string sni = settings.GetProperty("sni").GetString() ?? "google.com";
            string sid = settings.GetProperty("shortId").GetString() ?? "";
            string safeIp = ip.Contains(":") && !ip.StartsWith("[") ? $"[{ip}]" : ip;
            return $"vless://{uuid}@{safeIp}:{inbound.Port}?type=tcp&security=reality&pbk={pub}&fp=chrome&sni={sni}&sid={sid}&spx=%2F&flow=xtls-rprx-vision&alpn=h2#KoFF_{email}";
        } catch { return "Ошибка генерации ссылки"; }
    }

    private string GenerateHysteriaLinkFallback(ServerInbound inbound, string ip, string uuid, string email)
    {
        try {
            var settings = System.Text.Json.JsonDocument.Parse(inbound.SettingsJson).RootElement;
            string sni = settings.GetProperty("sni").GetString() ?? "bing.com";
            string obfs = settings.GetProperty("obfsPassword").GetString() ?? "";
            string safeIp = ip.Contains(":") && !ip.StartsWith("[") ? $"[{ip}]" : ip;
            string encodedName = Uri.EscapeDataString($"KoFF_{email}");
            return $"hy2://{uuid}@{safeIp}:{inbound.Port}?sni={sni}&obfs=salamander&obfs-password={obfs}&insecure=1#{encodedName}";
        } catch { return "Ошибка генерации ссылки"; }
    }

    private string GenerateTrustTunnelLinkFallback(ServerInbound inbound, string ip, string uuid, string email)
    {
        string safeIp = ip.Contains(":") && !ip.StartsWith("[") ? $"[{ip}]" : ip;
        return $"vless://{uuid}@{safeIp}:{inbound.Port}?type=xhttp&security=tls&sni=google.com&alpn=h3#TT_{email}";
    }

    private void ExtractTrustTunnelSettingsSafe(IEnumerable<ServerInbound> inbounds)
    {
        var ttInbound = inbounds.FirstOrDefault(i => i.Protocol.Equals("trusttunnel", StringComparison.OrdinalIgnoreCase));

        if (ttInbound is null || string.IsNullOrWhiteSpace(ttInbound.SettingsJson))
        {
            SetDefaultTrustTunnelSettings();
            return; // Early return (Guard clause)
        }

        try
        {
            var ttSettings = System.Text.Json.JsonDocument.Parse(ttInbound.SettingsJson).RootElement;
            TtDomainName = ttSettings.GetProperty("sni").GetString() ?? "google.com";
            TtDnsServers = "8.8.8.8, 1.1.1.1";
        }
        catch (System.Text.Json.JsonException)
        {
            // ИСПРАВЛЕНИЕ: Никаких пустых catch! (Silent fail обрабатывается fallback-значениями)
            SetDefaultTrustTunnelSettings();
        }
    }

    private void SetDefaultTrustTunnelSettings()
    {
        TtDomainName = "google.com";
        TtDnsServers = "8.8.8.8, 1.1.1.1";
    }

    [RelayCommand]
    private async Task CopyTtPasswordAsync()
    {
        if (string.IsNullOrWhiteSpace(TtPassword)) return;
        await SafeCopyToClipboardAsync(TtPassword);
        IsTrustTunnelCopied = true;
        await Task.Delay(2000);
        IsTrustTunnelCopied = false;
    }

    [RelayCommand]
    private async Task DownloadCertAsync()
    {
        if (!IsAdmin || !_ssh.IsConnected) return;

        try
        {
            string remotePath = TrustTunnelCertPath;
            string? localPath = _filePicker.SaveFile("cert.pem", "PEM Certificate (*.pem)|*.pem|All files (*.*)|*.*");
            
            if (string.IsNullOrEmpty(localPath)) return;

            using (var localStream = System.IO.File.Create(localPath))
            {
                await _ssh.DownloadFileAsync(remotePath, localStream);
            }
            
            MessageBox.Show($"Файл успешно сохранен: {localPath}", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка при скачивании: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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