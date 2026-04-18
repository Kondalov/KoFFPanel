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
using CommunityToolkit.Mvvm.Messaging;
using System.Linq;

namespace KoFFPanel.Presentation.ViewModels;

public partial class BotViewModel : ObservableObject
{
    private readonly IAppLogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(5) };

    [ObservableProperty] private string _botIpAddress = "127.0.0.1";
    [ObservableProperty] private string _botPort = "5000";
    [ObservableProperty] private string _apiSecret = "";
    [ObservableProperty] private int _reserveKeysCount = 0;

    [ObservableProperty] private string _botStatus = "Ожидание проверки...";
    [ObservableProperty] private bool _isBotOnline = false;
    [ObservableProperty] private int _pendingUsersCount = 0;

    [ObservableProperty] private bool _isSyncing = false;
    [ObservableProperty] private bool _isAutoSyncEnabled = false;

    [ObservableProperty] private int _totalBotUsers = 0;
    [ObservableProperty] private string _lastNightlySyncText = "Синхронизация еще не проводилась";

    private readonly DispatcherTimer _autoSyncTimer;
    private readonly DispatcherTimer _nightlyTimer;
    private readonly DispatcherTimer _heartbeatTimer;
    private DateTime _lastNightSyncDate = DateTime.MinValue;

    private readonly string _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "bot_config.json");

    public BotViewModel(IAppLogger logger, IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;

        _nightlyTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _nightlyTimer.Tick += async (s, e) => await SmartNightlyRoutineAsync(false);

        _autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _autoSyncTimer.Tick += async (s, e) => await SilentAutoSyncAsync();

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _heartbeatTimer.Tick += async (s, e) => await HeartbeatCheckAsync();

        LoadSettings();

        _nightlyTimer.Start();
        _heartbeatTimer.Start();

        _ = CheckBotStatusAsync();
        _ = SmartNightlyRoutineAsync(true);
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

    private async Task HeartbeatCheckAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiSecret)) return;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, GetApiUrl("/sync/pending"));
            request.Headers.Add("X-API-KEY", ApiSecret);
            var response = await _httpClient.SendAsync(request);

            string rawPendingContent = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode)
            {
                try
                {
                    var pendingUsers = JsonSerializer.Deserialize<List<PendingUserDto>>(rawPendingContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    PendingUsersCount = pendingUsers?.Count ?? 0;
                }
                catch (Exception jsonEx)
                {
                    _logger.Log("API-DIAGNOSTIC", $"[КРИТИЧЕСКАЯ ОШИБКА ПАРСИНГА] {jsonEx.Message}");
                }

                var poolReq = new HttpRequestMessage(HttpMethod.Get, GetApiUrl("/sync/pool/count"));
                poolReq.Headers.Add("X-API-KEY", ApiSecret);
                var poolRes = await _httpClient.SendAsync(poolReq);

                string rawPoolContent = await poolRes.Content.ReadAsStringAsync();

                if (poolRes.IsSuccessStatusCode)
                {
                    try
                    {
                        var poolData = JsonSerializer.Deserialize<ReserveCountDto>(rawPoolContent, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        ReserveKeysCount = poolData?.ReserveCount ?? 0;
                    }
                    catch { }
                }

                if (!IsBotOnline || BotStatus != "ОНЛАЙН")
                {
                    IsBotOnline = true;
                    BotStatus = "ОНЛАЙН";
                }
            }
            else
            {
                IsBotOnline = false;
                BotStatus = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "ОШИБКА АВТОРИЗАЦИИ"
                    : $"ОШИБКА API: {(int)response.StatusCode}";
            }
        }
        catch (Exception)
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

    [RelayCommand]
    private async Task RefreshStatsAsync()
    {
        await SmartNightlyRoutineAsync(force: true);
    }

    private async Task SmartNightlyRoutineAsync(bool force = false)
    {
        if (string.IsNullOrWhiteSpace(ApiSecret)) return;

        var now = DateTime.Now;
        if (!force && !(now.Hour >= 2 && now.Hour <= 4 && _lastNightSyncDate.Date != now.Date)) return;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, GetApiUrl("/stats"));
            request.Headers.Add("X-API-KEY", ApiSecret);

            var response = await _httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var stats = await response.Content.ReadFromJsonAsync<BotStatsDto>();

                TotalBotUsers = stats?.TotalUsers ?? 0;
                _lastNightSyncDate = now.Date;
                LastNightlySyncText = $"Обновлено: {now:dd.MM.yy HH:mm}";

                if (!IsBotOnline || BotStatus != "ОНЛАЙН")
                {
                    IsBotOnline = true;
                    BotStatus = "ОНЛАЙН";
                }
            }
            else
            {
                IsBotOnline = false;
                BotStatus = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "ОШИБКА АВТОРИЗАЦИИ"
                    : $"ОШИБКА API: {(int)response.StatusCode}";
            }
        }
        catch (Exception ex)
        {
            if (force) MessageBox.Show($"Не удалось получить данные: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // === ИСПРАВЛЕНИЕ АРХИТЕКТУРЫ: Единая точка тихой синхронизации ===
    private async Task SilentAutoSyncAsync()
    {
        if (!IsBotOnline) return;
        try
        {
            await SyncPendingUsersAsync();
            await SyncTrafficToBotAsync(); // Вызываем отправку трафика сразу после сверки юзеров
        }
        catch { }
    }

    // === НОВЫЙ МЕТОД: Отправка расхода трафика в бота ===
    private async Task SyncTrafficToBotAsync()
    {
        try
        {
            var cabinetVm = _serviceProvider.GetService<CabinetViewModel>();
            if (cabinetVm == null || cabinetVm.Clients == null || cabinetVm.Clients.Count == 0) return;

            // Собираем пакет данных: берем всех активных юзеров из UI
            var trafficPayload = cabinetVm.Clients
                .Where(c => !string.IsNullOrEmpty(c.Uuid))
                .Select(c => new TrafficSyncDto
                {
                    Uuid = c.Uuid,
                    TrafficLimitBytes = c.TrafficLimit, // Если 0 - это безлимит!
                    TrafficUsedBytes = c.TrafficUsed
                }).ToList();

            if (!trafficPayload.Any()) return;

            var req = new HttpRequestMessage(HttpMethod.Post, GetApiUrl("/sync/traffic"))
            {
                Content = JsonContent.Create(trafficPayload)
            };
            req.Headers.Add("X-API-KEY", ApiSecret);

            var response = await _httpClient.SendAsync(req);
            if (response.IsSuccessStatusCode)
            {
                _logger.Log("BOT-SYNC", $"Отправлен срез трафика для {trafficPayload.Count} клиентов.");
            }
        }
        catch (Exception ex)
        {
            _logger.Log("BOT-SYNC-ERROR", $"Ошибка при отправке трафика в бота: {ex.Message}");
        }
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
            bool needsDeployment = false;

            foreach (var pUser in pendingUsers)
            {
                var existingClient = dbContext.Clients.FirstOrDefault(c => c.Uuid == pUser.Uuid);

                if (existingClient != null)
                {
                    existingClient.Email = pUser.Email;
                    existingClient.TrafficLimit = pUser.TrafficLimitBytes;
                    existingClient.ExpiryDate = pUser.ExpiryDate;
                    existingClient.IsActive = pUser.IsActive;

                    var uiClient = cabinetVm.Clients.FirstOrDefault(c => c.Uuid == pUser.Uuid);
                    if (uiClient != null)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() =>
                        {
                            uiClient.Email = pUser.Email;
                            uiClient.TrafficLimit = pUser.TrafficLimitBytes;
                            uiClient.ExpiryDate = pUser.ExpiryDate;
                            uiClient.IsActive = pUser.IsActive;
                        });
                    }
                }
                else
                {
                    var newClient = new VpnClient
                    {
                        Uuid = pUser.Uuid,
                        Email = pUser.Email,
                        ServerIp = pUser.ServerIp,
                        TrafficLimit = pUser.TrafficLimitBytes,
                        IsActive = pUser.IsActive,
                        Protocol = "VLESS",
                        IsVlessEnabled = true,
                        IsP2PBlocked = true,
                        ExpiryDate = pUser.ExpiryDate
                    };
                    dbContext.Clients.Add(newClient);

                    if (cabinetVm.SelectedServer?.IpAddress == pUser.ServerIp)
                    {
                        System.Windows.Application.Current.Dispatcher.Invoke(() => cabinetVm.Clients.Add(newClient));
                    }
                }

                syncedUuids.Add(pUser.Uuid);
                needsDeployment = true;
            }

            await dbContext.SaveChangesAsync();

            var commitReq = new HttpRequestMessage(HttpMethod.Post, GetApiUrl("/sync/commit"));
            commitReq.Headers.Add("X-API-KEY", ApiSecret);
            commitReq.Content = JsonContent.Create(new CommitRequestDto { Uuids = syncedUuids });
            await _httpClient.SendAsync(commitReq);

            if (needsDeployment && cabinetVm.SelectedServer != null)
            {
                WeakReferenceMessenger.Default.Send(new KoFFPanel.Presentation.Messages.CoreDeployedMessage(cabinetVm.SelectedServer));
            }

            _logger.Log("BOT-SYNC", $"Синхронизировано {syncedUuids.Count} юзеров.");
            PendingUsersCount = 0;
        }
        finally
        {
            IsSyncing = false;
            BotStatus = oldStatus;
            await HeartbeatCheckAsync();
        }
    }

    [RelayCommand]
    private async Task RefillReservePoolAsync()
    {
        if (!IsBotOnline) return;

        var cabinetVm = _serviceProvider.GetRequiredService<CabinetViewModel>();
        var server = cabinetVm.SelectedServer;

        if (server == null || cabinetVm.ServerStatus == "Ожидание...")
        {
            MessageBox.Show("Сначала выберите сервер во вкладке 'Сервер' и дождитесь подключения мониторинга!", "Внимание", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int keysNeeded = 5 - ReserveKeysCount;
        if (keysNeeded <= 0) return;

        IsSyncing = true;
        string oldStatus = BotStatus;
        BotStatus = $"Создаем {keysNeeded} резервных ключей...";

        try
        {
            var dbContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
            var keysToPush = new List<ReserveKeyDto>();

            for (int i = 0; i < keysNeeded; i++)
            {
                string newUuid = Guid.NewGuid().ToString();
                string tempEmail = $"res_{newUuid.Substring(0, 5)}";
                long tempLimit = 1024L * 1024 * 1024;

                var newClient = new VpnClient
                {
                    Uuid = newUuid,
                    Email = tempEmail,
                    ServerIp = server.IpAddress,
                    TrafficLimit = tempLimit,
                    IsActive = true,
                    Protocol = "VLESS",
                    ExpiryDate = DateTime.Now.AddDays(3),
                    IsVlessEnabled = true,
                    IsP2PBlocked = true
                };

                dbContext.Clients.Add(newClient);
                keysToPush.Add(new ReserveKeyDto { Uuid = newUuid, ServerIp = server.IpAddress, TrafficLimitBytes = tempLimit });

                System.Windows.Application.Current.Dispatcher.Invoke(() => cabinetVm.Clients.Add(newClient));
            }

            await dbContext.SaveChangesAsync();

            var req = new HttpRequestMessage(HttpMethod.Post, GetApiUrl("/sync/pool"));
            req.Headers.Add("X-API-KEY", ApiSecret);
            req.Content = JsonContent.Create(keysToPush);
            await _httpClient.SendAsync(req);

            WeakReferenceMessenger.Default.Send(new KoFFPanel.Presentation.Messages.CoreDeployedMessage(server));

            ReserveKeysCount += keysNeeded;
            _logger.Log("BOT-POOL", $"Успешно добавлено {keysNeeded} ключей в резерв.");
        }
        finally
        {
            IsSyncing = false;
            BotStatus = oldStatus;
            await HeartbeatCheckAsync();
        }
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

public class PendingUserDto
{
    public string Uuid { get; set; } = "";
    public string Email { get; set; } = "";
    public string ServerIp { get; set; } = "";
    public long TrafficLimitBytes { get; set; }
    public bool IsActive { get; set; }
    public DateTime? ExpiryDate { get; set; }
}

public class LegacyUserDto { public string Uuid { get; set; } = ""; public string Email { get; set; } = ""; public string ServerIp { get; set; } = ""; public long TrafficLimitBytes { get; set; } public DateTime? ExpiryDate { get; set; } }
public class BotStatsDto { public int TotalUsers { get; set; } }
public class ReserveCountDto { public int ReserveCount { get; set; } }
public class ReserveKeyDto { public string Uuid { get; set; } = ""; public string ServerIp { get; set; } = ""; public long TrafficLimitBytes { get; set; } }

// НОВЫЙ DTO: Для отправки расхода трафика в бота
public class TrafficSyncDto
{
    public string Uuid { get; set; } = "";
    public long TrafficUsedBytes { get; set; }
    public long TrafficLimitBytes { get; set; }
}