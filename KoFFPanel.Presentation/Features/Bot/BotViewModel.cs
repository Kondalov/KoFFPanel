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
using Microsoft.EntityFrameworkCore;
using KoFFPanel.Presentation.Features.Bot;
using KoFFPanel.Presentation.Messages;
using KoFFPanel.Presentation.Features.Cabinet;

namespace KoFFPanel.Presentation.Features.Bot;

public partial class BotViewModel : ObservableObject
{
    private readonly IAppLogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMarzbanMigrationService _migrationService;

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

    public BotViewModel(IAppLogger logger, IServiceProvider serviceProvider, IHttpClientFactory httpClientFactory, IMarzbanMigrationService migrationService)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _httpClientFactory = httpClientFactory;
        _migrationService = migrationService;

        _nightlyTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(30) };
        _nightlyTimer.Tick += (s, e) => SafeFireAndForget(() => SmartNightlyRoutineAsync(false));

        _autoSyncTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _autoSyncTimer.Tick += (s, e) => SafeFireAndForget(() => SilentAutoSyncAsync());

        _heartbeatTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _heartbeatTimer.Tick += (s, e) => SafeFireAndForget(() => HeartbeatCheckAsync());

        LoadSettings();

        _nightlyTimer.Start();
        _heartbeatTimer.Start();

        SafeFireAndForget(() => CheckBotStatusAsync());
        SafeFireAndForget(() => SmartNightlyRoutineAsync(true));
    }

    partial void OnIsAutoSyncEnabledChanged(bool value)
    {
        if (_autoSyncTimer == null) return;
        if (value) _autoSyncTimer.Start(); else _autoSyncTimer.Stop();
        SaveSettings();
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
            var response = await GetClient().SendAsync(request);
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
            await GetClient().SendAsync(commitReq);

            if (needsDeployment && cabinetVm.SelectedServer != null)
            {
                WeakReferenceMessenger.Default.Send(new CoreDeployedMessage(cabinetVm.SelectedServer));
            }

            _logger.Log("BOT-SYNC", $"Синхронизировано {syncedUuids.Count} юзеров.");
            PendingUsersCount = 0;
        }
        catch (Exception ex)
        {
            _logger.Log("BOT-SYNC-ERR", $"Ошибка синхронизации: {ex.Message}");
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
                    ServerIp = BotIpAddress,
                    TrafficLimit = tempLimit,
                    IsActive = true,
                    Protocol = "VLESS",
                    ExpiryDate = DateTime.Now.AddDays(3),
                    IsVlessEnabled = true,
                    IsP2PBlocked = true
                };

                dbContext.Clients.Add(newClient);
                keysToPush.Add(new ReserveKeyDto { Uuid = newUuid, ServerIp = BotIpAddress, TrafficLimitBytes = tempLimit });

                System.Windows.Application.Current.Dispatcher.Invoke(() => cabinetVm.Clients.Add(newClient));
            }

            await dbContext.SaveChangesAsync();

            var req = new HttpRequestMessage(HttpMethod.Post, GetApiUrl("/sync/pool"));
            req.Headers.Add("X-API-KEY", ApiSecret);
            req.Content = JsonContent.Create(keysToPush);
            await GetClient().SendAsync(req);

            WeakReferenceMessenger.Default.Send(new CoreDeployedMessage(server));

            ReserveKeysCount += keysNeeded;
            _logger.Log("BOT-POOL", $"Успешно добавлено {keysNeeded} ключей в резерв.");
        }
        catch (Exception ex)
        {
            _logger.Log("BOT-POOL-ERR", $"Ошибка при добавлении ключей в резерв: {ex.Message}");
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
            await GetClient().SendAsync(req);

            MessageBox.Show("Конфигурация сервера успешно отправлена!", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка отправки конфигурации: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
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
                allClients.Add(new LegacyUserDto { Uuid = c.Uuid ?? "", Email = c.Email ?? "", ServerIp = c.ServerIp ?? "", TrafficLimitBytes = c.TrafficLimit, ReffererId = c.ReffererId, ExpiryDate = c.ExpiryDate });

            var req = new HttpRequestMessage(HttpMethod.Post, GetApiUrl("/legacy/sync")) { Content = JsonContent.Create(allClients) };
            req.Headers.Add("X-API-KEY", ApiSecret);
            var res = await GetClient().SendAsync(req);

            if (res.IsSuccessStatusCode) MessageBox.Show($"База выгружена! Передано {allClients.Count} клиентов.", "Успех", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Ошибка выгрузки базы: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { IsSyncing = false; await HeartbeatCheckAsync(); }
    }

    [RelayCommand]
    private async Task MigrateFromMarzbanAsync()
    {
        string marzbanPath = @"C:\Users\Nikolay\OneDrive\Документы\ShareX\Screenshots\2026-04\TESTS\backup_2026-04-30_12-00-01\marzban_db.sql";
        string botPath = @"C:\Users\Nikolay\OneDrive\Документы\ShareX\Screenshots\2026-04\TESTS\backup_2026-04-30_12-00-01\bot_db.sql";

        var cabinetVm = _serviceProvider.GetRequiredService<CabinetViewModel>();
        string serverIp = cabinetVm.SelectedServer?.IpAddress ?? "103.71.22.166";

        var result = MessageBox.Show($"Вы уверены, что хотите запустить миграцию пользователей из Marzban для сервера {serverIp}?\n\nПуть к Marzban: {marzbanPath}", 
            "Подтверждение миграции", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes) return;

        IsSyncing = true;
        string oldStatus = BotStatus;
        BotStatus = "Миграция БД...";

        try
        {
            var (isSuccess, message) = await _migrationService.MigrateAsync(marzbanPath, botPath, serverIp);

            if (isSuccess)
            {
                MessageBox.Show(message + "\n\nВсе реферальные связи сохранены в базе и будут переданы в бота при выгрузке.", "Миграция завершена", MessageBoxButton.OK, MessageBoxImage.Information);
                
                // Обновляем список клиентов в UI
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(async () => {
                    if (cabinetVm.SelectedServer != null)
                    {
                        // Перезагружаем список пользователей для выбранного сервера
                        var userManager = _serviceProvider.GetRequiredService<IXrayUserManagerService>();
                        var ssh = _serviceProvider.GetRequiredService<ISshService>();
                        // Если SSH не подключен, просто перечитаем из БД
                        var dbContext = _serviceProvider.GetRequiredService<KoFFPanel.Infrastructure.Data.AppDbContext>();
                        var updatedUsers = await dbContext.Clients.Where(c => c.ServerIp == serverIp).ToListAsync();
                        
                        cabinetVm.Clients.Clear();
                        foreach (var user in updatedUsers) cabinetVm.Clients.Add(user);
                    }
                });
            }
            else
            {
                MessageBox.Show(message, "Ошибка миграции", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Критическая ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsSyncing = false;
            BotStatus = oldStatus;
        }
    }
}
