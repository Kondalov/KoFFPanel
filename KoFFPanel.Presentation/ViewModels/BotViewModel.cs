using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using System.Windows.Threading;

namespace KoFFPanel.Presentation.ViewModels;

public partial class BotViewModel : ObservableObject
{
    private readonly IAppLogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    [ObservableProperty] private string _botIpAddress = "127.0.0.1";
    [ObservableProperty] private string _botPort = "5000";
    [ObservableProperty] private string _apiSecret = "";

    [ObservableProperty] private string _botStatus = "Ожидание проверки...";
    [ObservableProperty] private bool _isBotOnline = false;
    [ObservableProperty] private int _pendingUsersCount = 0;

    [ObservableProperty] private bool _isSyncing = false;
    [ObservableProperty] private bool _isAutoSyncEnabled = false;

    [ObservableProperty] private int _totalBotUsers = 0;
    [ObservableProperty] private string _lastNightlySyncText = "Синхронизация еще не проводилась";

    private readonly DispatcherTimer _autoSyncTimer;
    private readonly DispatcherTimer _nightlyTimer;
    private readonly DispatcherTimer _heartbeatTimer; // Умный пульс
    private DateTime _lastNightSyncDate = DateTime.MinValue;

    private readonly string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bot_config.json");

    public BotViewModel(IAppLogger logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;

        // Таймер 1: Умная ночная работа (каждые 30 мин)
        _nightlyTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _nightlyTimer.Tick += async (s, e) => await SmartNightlyRoutineAsync(false);

        // Таймер 2: Опрос очереди (каждые 2 мин)
        _autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _autoSyncTimer.Tick += async (s, e) => await SilentAutoSyncAsync();

        // Таймер 3: Пульс бота (каждые 15 секунд защита от обрывов)
        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _heartbeatTimer.Tick += async (s, e) => await HeartbeatCheckAsync();

        LoadSettings();

        _nightlyTimer.Start();
        _heartbeatTimer.Start();

        // Авто-запуск проверок при загрузке панели
        _ = CheckBotStatusAsync();
        _ = SmartNightlyRoutineAsync(true); // Запрашиваем юзеров сразу при старте
    }

    partial void OnIsAutoSyncEnabledChanged(bool value)
    {
        if (_autoSyncTimer == null) return;
        if (value) _autoSyncTimer.Start(); else _autoSyncTimer.Stop();
        SaveSettings();
    }

    private string GetApiUrl(string endpoint) => $"http://{BotIpAddress}:{BotPort}/api{endpoint}";

    private void SaveSettings()
    {
        try
        {
            var config = new { BotIp = BotIpAddress, BotPort = BotPort, BotApiSecret = ApiSecret, AutoSync = IsAutoSyncEnabled };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var config = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(_configPath));
                BotIpAddress = config.TryGetProperty("BotIp", out var ip) ? ip.GetString() ?? "127.0.0.1" : "127.0.0.1";
                BotPort = config.TryGetProperty("BotPort", out var port) ? port.GetString() ?? "5000" : "5000";
                ApiSecret = config.TryGetProperty("BotApiSecret", out var sec) ? sec.GetString() ?? "" : "";
                IsAutoSyncEnabled = config.TryGetProperty("AutoSync", out var auto) && auto.GetBoolean();
            }
        }
        catch { }
    }

    // === УМНЫЙ ПУЛЬС (Автоматическое поддержание статуса) ===
    private async Task HeartbeatCheckAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiSecret)) return;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, GetApiUrl("/sync/pending"));
            request.Headers.Add("X-API-KEY", ApiSecret);
            var response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
            {
                var pendingUsers = await response.Content.ReadFromJsonAsync<List<PendingUserDto>>();
                PendingUsersCount = pendingUsers?.Count ?? 0;

                if (!IsBotOnline || BotStatus != "ОНЛАЙН")
                {
                    IsBotOnline = true;
                    BotStatus = "ОНЛАЙН";
                }
            }
            else
            {
                IsBotOnline = false;
                BotStatus = "ОШИБКА АВТОРИЗАЦИИ";
            }
        }
        catch
        {
            IsBotOnline = false;
            BotStatus = "ОФФЛАЙН (Недоступен)";
        }
    }

    [RelayCommand]
    private async Task CheckBotStatusAsync()
    {
        SaveSettings();
        BotStatus = "Проверка...";
        await HeartbeatCheckAsync();
    }

    // === ПРИНУДИТЕЛЬНОЕ ОБНОВЛЕНИЕ СТАТИСТИКИ ===
    [RelayCommand]
    private async Task RefreshStatsAsync()
    {
        await SmartNightlyRoutineAsync(force: true);
    }

    // === УМНЫЙ АЛГОРИТМ АНАЛИТИКИ ===
    private async Task SmartNightlyRoutineAsync(bool force = false)
    {
        if (string.IsNullOrWhiteSpace(ApiSecret)) return;

        var now = DateTime.Now;
        // Защита от дурака: если не принудительно, проверяем ночное время
        if (!force && !(now.Hour >= 2 && now.Hour <= 4 && _lastNightSyncDate.Date != now.Date)) return;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, GetApiUrl("/stats"));
            request.Headers.Add("X-API-KEY", ApiSecret);

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var stats = await response.Content.ReadFromJsonAsync<BotStatsDto>();
                if (stats != null)
                {
                    TotalBotUsers = stats.TotalUsers;
                    _lastNightSyncDate = now.Date;
                    LastNightlySyncText = $"Обновлено: {now:dd.MM.yy HH:mm}";
                    _logger.Log("BOT-STATS", $"Статистика обновлена: {TotalBotUsers} юзеров.");
                }
            }
        }
        catch (Exception ex)
        {
            if (force) MessageBox.Show($"Не удалось получить данные: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async Task SilentAutoSyncAsync()
    {
        if (!IsBotOnline) return;
        try { await SyncPendingUsersAsync(); } catch { }
    }

    [RelayCommand]
    private async Task SyncPendingUsersAsync()
    {
        if (!IsBotOnline || PendingUsersCount == 0) return;
        IsSyncing = true;
        string oldStatus = BotStatus;
        BotStatus = "Вшиваем ключи...";

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, GetApiUrl("/sync/pending"));
            request.Headers.Add("X-API-KEY", ApiSecret);
            var response = await _httpClient.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;

            var pendingUsers = await response.Content.ReadFromJsonAsync<List<PendingUserDto>>();
            if (pendingUsers == null || pendingUsers.Count == 0) return;

            var cabinetVm = _serviceProvider.GetRequiredService<CabinetViewModel>();
            var dbContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
            var syncedUuids = new List<string>();

            foreach (var pUser in pendingUsers)
            {
                var newClient = new VpnClient
                {
                    Uuid = pUser.Uuid,
                    Email = pUser.Email,
                    ServerIp = pUser.ServerIp,
                    TrafficLimit = pUser.TrafficLimitBytes,
                    IsActive = pUser.IsActive,
                    Protocol = "Offline"
                };
                dbContext.Clients.Add(newClient);
                syncedUuids.Add(pUser.Uuid);

                if (cabinetVm.SelectedServer?.IpAddress == pUser.ServerIp)
                    System.Windows.Application.Current.Dispatcher.Invoke(() => cabinetVm.Clients.Add(newClient));
            }
            await dbContext.SaveChangesAsync();

            var commitReq = new HttpRequestMessage(HttpMethod.Post, GetApiUrl("/sync/commit"));
            commitReq.Headers.Add("X-API-KEY", ApiSecret);
            commitReq.Content = JsonContent.Create(new CommitRequestDto { Uuids = syncedUuids });
            await _httpClient.SendAsync(commitReq);

            if (cabinetVm.SelectedServer != null)
                cabinetVm.Receive(new Messages.CoreDeployedMessage(cabinetVm.SelectedServer));

            _logger.Log("BOT-SYNC", $"Синхронизировано {syncedUuids.Count} юзеров.");
            PendingUsersCount = 0;
        }
        finally { IsSyncing = false; BotStatus = oldStatus; }
    }

    [RelayCommand]
    private async Task PushTemplateToBotAsync()
    {
        if (!IsBotOnline) return;
        var cabinetVm = _serviceProvider.GetRequiredService<CabinetViewModel>();
        if (cabinetVm.SelectedServer == null) return;

        try
        {
            IsSyncing = true;
            var botInbounds = new List<object>();
            foreach (var inbound in cabinetVm.SelectedServer.Inbounds)
            {
                string pubKey = "", sni = "", shortId = "";
                try
                {
                    var settings = JsonDocument.Parse(inbound.SettingsJson).RootElement;
                    if (settings.TryGetProperty("publicKey", out var pk)) pubKey = pk.GetString() ?? "";
                    if (settings.TryGetProperty("sni", out var s)) sni = s.GetString() ?? "";
                    if (settings.TryGetProperty("shortId", out var sid)) shortId = sid.GetString() ?? "";
                }
                catch { }

                botInbounds.Add(new { Protocol = inbound.Protocol, Port = inbound.Port, Sni = string.IsNullOrEmpty(sni) ? "microsoft.com" : sni, PublicKey = pubKey, ShortId = shortId });
            }

            var payload = new { ServerIp = cabinetVm.SelectedServer.IpAddress, CoreType = cabinetVm.SelectedServer.CoreType, InboundsConfigJson = JsonSerializer.Serialize(botInbounds) };
            var req = new HttpRequestMessage(HttpMethod.Post, GetApiUrl("/templates")) { Content = JsonContent.Create(payload) };
            req.Headers.Add("X-API-KEY", ApiSecret);
            await _httpClient.SendAsync(req);

            MessageBox.Show("Конфигурация сервера успешно отправлена!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { IsSyncing = false; }
    }

    [RelayCommand]
    private async Task SyncLegacyUsersToBotAsync()
    {
        if (!IsBotOnline) return;
        var cabinetVm = _serviceProvider.GetService<CabinetViewModel>();
        if (cabinetVm == null || cabinetVm.Clients == null || cabinetVm.Clients.Count == 0) return;

        try
        {
            IsSyncing = true; BotStatus = "Выгрузка базы...";
            var allClients = new List<LegacyUserDto>();

            foreach (var c in cabinetVm.Clients)
                allClients.Add(new LegacyUserDto { Uuid = c.Uuid ?? "", Email = c.Email ?? "", ServerIp = c.ServerIp ?? "", TrafficLimitBytes = c.TrafficLimit, ExpiryDate = c.ExpiryDate });

            var req = new HttpRequestMessage(HttpMethod.Post, GetApiUrl("/legacy/sync")) { Content = JsonContent.Create(allClients) };
            req.Headers.Add("X-API-KEY", ApiSecret);
            var res = await _httpClient.SendAsync(req);

            if (res.IsSuccessStatusCode) MessageBox.Show($"База выгружена! Передано {allClients.Count} клиентов.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { IsSyncing = false; await HeartbeatCheckAsync(); }
    }
}

public class CommitRequestDto { public List<string> Uuids { get; set; } = new(); }
public class PendingUserDto { public string Uuid { get; set; } = ""; public string Email { get; set; } = ""; public string ServerIp { get; set; } = ""; public long TrafficLimitBytes { get; set; } public bool IsActive { get; set; } }
public class LegacyUserDto { public string Uuid { get; set; } = ""; public string Email { get; set; } = ""; public string ServerIp { get; set; } = ""; public long TrafficLimitBytes { get; set; } public DateTime? ExpiryDate { get; set; } }
public class BotStatsDto { public int TotalUsers { get; set; } }