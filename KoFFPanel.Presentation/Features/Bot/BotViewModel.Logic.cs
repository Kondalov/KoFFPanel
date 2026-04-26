using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using CommunityToolkit.Mvvm.Messaging;
using System.Linq;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Presentation.Features.Bot;
using KoFFPanel.Presentation.Messages;
using KoFFPanel.Presentation.Features.Cabinet;

namespace KoFFPanel.Presentation.Features.Bot;

public partial class BotViewModel
{
    private string GetApiUrl(string endpoint) => $"http://{BotIpAddress}:{BotPort}/api{endpoint}";

    private HttpClient GetClient()
    {
        var client = _httpClientFactory.CreateClient("BotApiClient");
        if (client.Timeout == System.Threading.Timeout.InfiniteTimeSpan)
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        }
        return client;
    }

    private void SaveSettings()
    {
        try
        {
            string encryptedSecret = "";
            if (!string.IsNullOrWhiteSpace(ApiSecret))
            {
                var secretBytes = System.Text.Encoding.UTF8.GetBytes(ApiSecret);
                var encryptedBytes = System.Security.Cryptography.ProtectedData.Protect(secretBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                encryptedSecret = Convert.ToBase64String(encryptedBytes);
            }

            var config = new { BotIp = BotIpAddress, BotPort = BotPort, BotApiSecret = encryptedSecret, AutoSync = IsAutoSyncEnabled };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger?.Log("BOT-SETTINGS-ERR", $"Ошибка при сохранении настроек: {ex.Message}");
        }
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
                IsAutoSyncEnabled = config.TryGetProperty("AutoSync", out var auto) && auto.GetBoolean();

                string encryptedSecret = config.TryGetProperty("BotApiSecret", out var sec) ? sec.GetString() ?? "" : "";

                if (!string.IsNullOrWhiteSpace(encryptedSecret))
                {
                    try
                    {
                        var encryptedBytes = Convert.FromBase64String(encryptedSecret);
                        var secretBytes = System.Security.Cryptography.ProtectedData.Unprotect(encryptedBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                        ApiSecret = System.Text.Encoding.UTF8.GetString(secretBytes);
                    }
                    catch (Exception cryptoEx)
                    {
                        _logger?.Log("CRYPTO-ERR", $"Не удалось расшифровать API Secret: {cryptoEx.Message}");
                        ApiSecret = "";
                    }
                }
                else
                {
                    ApiSecret = "";
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.Log("BOT-SETTINGS-ERR", $"Ошибка при загрузке настроек: {ex.Message}");
        }
    }

    private async Task HeartbeatCheckAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiSecret)) return;
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, GetApiUrl("/sync/pending"));
            request.Headers.Add("X-API-KEY", ApiSecret);
            var response = await GetClient().SendAsync(request);

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
                var poolRes = await GetClient().SendAsync(poolReq);

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

    private async Task SmartNightlyRoutineAsync(bool force = false)
    {
        if (string.IsNullOrWhiteSpace(ApiSecret)) return;

        var now = DateTime.Now;
        if (!force && !(now.Hour >= 2 && now.Hour <= 4 && _lastNightSyncDate.Date != now.Date)) return;

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, GetApiUrl("/stats"));
            request.Headers.Add("X-API-KEY", ApiSecret);

            var response = await GetClient().SendAsync(request);
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

    private async Task SilentAutoSyncAsync()
    {
        if (!IsBotOnline) return;
        try
        {
            await SyncPendingUsersAsync();
            await SyncTrafficToBotAsync();
        }
        catch { }
    }

    private async Task SyncTrafficToBotAsync()
    {
        try
        {
            var cabinetVm = _serviceProvider.GetService<CabinetViewModel>();
            if (cabinetVm == null || cabinetVm.Clients == null || cabinetVm.Clients.Count == 0) return;

            var trafficPayload = cabinetVm.Clients
                .Where(c => !string.IsNullOrEmpty(c.Uuid))
                .Select(c => new TrafficSyncDto
                {
                    Uuid = c.Uuid,
                    TrafficLimitBytes = c.TrafficLimit,
                    TrafficUsedBytes = c.TrafficUsed,
                    ExpiryDate = c.ExpiryDate
                }).ToList();

            if (!trafficPayload.Any()) return;

            var req = new HttpRequestMessage(HttpMethod.Post, GetApiUrl("/sync/traffic"))
            {
                Content = JsonContent.Create(trafficPayload)
            };
            req.Headers.Add("X-API-KEY", ApiSecret);

            var response = await GetClient().SendAsync(req);
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

    private async void SafeFireAndForget(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger?.Log("BOT-CRASH-PREVENTED", $"Предотвращено падение приложения в фоне: {ex.Message}");
        }
    }
}
