using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class RealitySniScannerService : IRealitySniScannerService
{
    private readonly IAppLogger _logger;
    private readonly IProfileRepository _profileRepository;
    private readonly ISubscriptionService _subscriptionService;

    public static readonly string[] DefaultSniPool = new[]
    {
        "dl.google.com",
        "www.microsoft.com",
        "gateway.icloud.com",
        "speed.cloudflare.com",
        "swdist.apple.com",
        "www.amazon.com",
        "images.unsplash.com"
    };

    public RealitySniScannerService(IAppLogger logger, IProfileRepository profileRepository, ISubscriptionService subscriptionService)
    {
        _logger = logger;
        _profileRepository = profileRepository;
        _subscriptionService = subscriptionService;
    }

    public async Task<(bool IsAccessible, long LatencyMs, string Error)> CheckSniAsync(string sni, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(sni))
            return (false, -1, "SNI пуст");

        var sw = Stopwatch.StartNew();
        try
        {
            using var client = new TcpClient();
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(4));

            await client.ConnectAsync(sni, 443, linkedCts.Token);

            using var sslStream = new SslStream(client.GetStream(), false, (sender, cert, chain, errors) => true);
            var sslOptions = new SslClientAuthenticationOptions
            {
                TargetHost = sni,
                EnabledSslProtocols = SslProtocols.Tls13 | SslProtocols.Tls12
            };

            await sslStream.AuthenticateAsClientAsync(sslOptions, linkedCts.Token);
            sw.Stop();

            return (true, sw.ElapsedMilliseconds, "");
        }
        catch (Exception ex)
        {
            sw.Stop();
            return (false, sw.ElapsedMilliseconds, ex.Message);
        }
    }

    public async Task<(string BestSni, long LatencyMs)> FindBestSniAsync(IEnumerable<string>? candidatePool = null, CancellationToken token = default)
    {
        var candidates = (candidatePool ?? DefaultSniPool).Distinct().ToList();
        string bestSni = candidates.FirstOrDefault() ?? "dl.google.com";
        long minLatency = long.MaxValue;

        foreach (var sni in candidates)
        {
            if (token.IsCancellationRequested) break;

            var (accessible, latency, _) = await CheckSniAsync(sni, token);
            if (accessible && latency < minLatency)
            {
                minLatency = latency;
                bestSni = sni;
            }
        }

        return (bestSni, minLatency == long.MaxValue ? -1 : minLatency);
    }

    public async Task<(bool Success, string Message)> RotateSniAsync(ISshService ssh, VpnProfile profile, string newSni)
    {
        if (!ssh.IsConnected) return (false, "Нет подключения по SSH");
        if (profile == null) return (false, "Профиль не найден");

        try
        {
            string coreType = (profile.CoreType ?? "xray").ToLowerInvariant();
            string s = (await ssh.ExecuteCommandAsync("if [ \"$EUID\" -ne 0 ]; then echo 'sudo'; fi")).Trim();

            // 1. Находим VLESS Reality inbound
            var inbound = profile.Inbounds.FirstOrDefault(i => string.Equals(i.Protocol, "vless", StringComparison.OrdinalIgnoreCase));
            if (inbound == null || string.IsNullOrWhiteSpace(inbound.SettingsJson))
            {
                return (false, "Inbound VLESS Reality не найден в профиле сервера");
            }

            var settingsNode = JsonNode.Parse(inbound.SettingsJson);
            if (settingsNode == null) return (false, "Некорректный SettingsJson");

            string oldSni = settingsNode["sni"]?.ToString() ?? "google.com";
            settingsNode["sni"] = newSni;
            inbound.SettingsJson = settingsNode.ToJsonString();

            // 2. Обновляем конфигурационный файл на сервере
            if (coreType == "sing-box")
            {
                string configPath = "/etc/sing-box/config.json";
                string readJsonCmd = $"cat {configPath}";
                string currentConfig = await ssh.ExecuteCommandAsync(readJsonCmd);
                var configObj = JsonNode.Parse(currentConfig);
                if (configObj?["inbounds"] is JsonArray inboundsArr)
                {
                    foreach (var inb in inboundsArr)
                    {
                        if (inb?["type"]?.ToString() == "vless" && inb["tls"]?["reality"] != null)
                        {
                            inb["tls"]!["server_name"] = newSni;
                            inb["tls"]!["reality"]!["handshake"]!["server"] = newSni;
                        }
                    }

                    string newConfigJson = configObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(newConfigJson));
                    await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d | {s} tee /tmp/sb_test.json >/dev/null");
                    string testOut = await ssh.ExecuteCommandAsync($"{s} sing-box check -c /tmp/sb_test.json 2>&1");
                    if (testOut.Contains("FATAL", StringComparison.OrdinalIgnoreCase) || testOut.Contains("error", StringComparison.OrdinalIgnoreCase))
                    {
                        return (false, $"Sing-box отклонил обновленный SNI: {testOut}");
                    }

                    await ssh.ExecuteCommandAsync($"{s} mv /tmp/sb_test.json {configPath} && {s} systemctl restart sing-box");
                }
            }
            else
            {
                // Xray
                string configPath = "/usr/local/etc/xray/config.json";
                string readJsonCmd = $"cat {configPath}";
                string currentConfig = await ssh.ExecuteCommandAsync(readJsonCmd);
                var configObj = JsonNode.Parse(currentConfig);
                if (configObj?["inbounds"] is JsonArray inboundsArr)
                {
                    foreach (var inb in inboundsArr)
                    {
                        if (inb?["protocol"]?.ToString() == "vless" && inb["streamSettings"]?["realitySettings"] != null)
                        {
                            inb["streamSettings"]!["realitySettings"]!["dest"] = $"{newSni}:443";
                            inb["streamSettings"]!["realitySettings"]!["serverNames"] = new JsonArray { newSni };
                        }
                    }

                    string newConfigJson = configObj.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                    string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(newConfigJson));
                    await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d | {s} tee /tmp/xray_test.json >/dev/null");
                    string testOut = await ssh.ExecuteCommandAsync($"{s} /usr/local/bin/xray run -test -config /tmp/xray_test.json 2>&1");
                    if (!testOut.Contains("Configuration OK"))
                    {
                        return (false, $"Xray отклонил обновленный SNI: {testOut}");
                    }

                    await ssh.ExecuteCommandAsync($"{s} mv /tmp/xray_test.json {configPath} && {s} systemctl restart xray");
                }
            }

            // 3. Сохраняем обновленный профиль в репозиторий
            _profileRepository.UpdateProfile(profile);

            _logger.Log("SNI-ROTATE", $"Успешно выполнен переход со старого SNI '{oldSni}' на новый '{newSni}' на сервере {profile.IpAddress}");
            return (true, $"SNI успешно изменен на {newSni}");
        }
        catch (Exception ex)
        {
            _logger.Log("SNI-ROTATE-ERR", $"Ошибка ротации SNI: {ex.Message}");
            return (false, ex.Message);
        }
    }
}
