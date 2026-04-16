using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class CoreDeploymentService : ICoreDeploymentService
{
    private readonly IAppLogger _logger;
    private readonly IProfileRepository _profileRepository;

    public CoreDeploymentService(IAppLogger logger, IProfileRepository profileRepository)
    {
        _logger = logger;
        _profileRepository = profileRepository;
    }

    public async Task<(bool IsSuccess, string Message)> RunPreFlightChecksAsync(ISshService ssh)
    {
        _logger.Log("DEPLOY-TRACE", "[PRE-FLIGHT] Запуск базовых проверок ОС...");
        if (!ssh.IsConnected) return (false, "Нет SSH подключения");

        string checkScript = @"
if [ ""$EUID"" -ne 0 ]; then echo 'ERROR|Нужны права root (sudo).'; exit 0; fi
if ! command -v systemctl >/dev/null 2>&1; then echo 'ERROR|Сервер не поддерживает systemd.'; exit 0; fi
if ! command -v curl >/dev/null 2>&1; then echo 'ERROR|Пакет curl не установлен.'; exit 0; fi
echo 'READY|Сервер готов к установке.'
";
        string result = (await ssh.ExecuteCommandAsync(checkScript)).Trim();
        _logger.Log("DEPLOY-TRACE", $"[PRE-FLIGHT] Результат: {result}");

        if (result.StartsWith("ERROR|")) return (false, result.Split('|')[1]);
        return (true, "Сервер готов.");
    }

    public async Task<string> GetInstalledXrayVersionAsync(ISshService ssh)
    {
        if (!ssh.IsConnected) return "Отключен";
        string result = await ssh.ExecuteCommandAsync("xray version | head -n 1 | awk '{print $2}'");
        return string.IsNullOrWhiteSpace(result) ? "Не установлено" : result.Trim();
    }

    public async Task<string> GetInstalledSingBoxVersionAsync(ISshService ssh)
    {
        if (!ssh.IsConnected) return "Отключен";
        string result = await ssh.ExecuteCommandAsync("sing-box version | grep 'version' | awk '{print $3}'");
        return string.IsNullOrWhiteSpace(result) ? "Не установлено" : result.Trim();
    }

    public async Task<string> GetInstalledTrustTunnelVersionAsync(ISshService ssh)
    {
        if (!ssh.IsConnected) return "Отключен";
        string result = await ssh.ExecuteCommandAsync("trusttunnel --version 2>/dev/null | awk '{print $2}'");
        return string.IsNullOrWhiteSpace(result) ? "Не установлено" : result.Trim();
    }

    public async Task<(bool IsSuccess, string Log)> InstallXrayAsync(ISshService ssh, string targetVersion = "latest")
    {
        _logger.Log("DEPLOY-TRACE", $"[INSTALL] Скачивание и установка Xray-core ({targetVersion})...");
        string installCmd = targetVersion == "latest"
            ? "bash -c \"$(curl -L https://github.com/XTLS/Xray-install/raw/main/install-release.sh)\" @ install"
            : $"bash -c \"$(curl -L https://github.com/XTLS/Xray-install/raw/main/install-release.sh)\" @ install --version {targetVersion}";

        string log = await ssh.ExecuteCommandAsync(installCmd, TimeSpan.FromMinutes(3));
        return (log.Contains("installed") || log.Contains("success"), log);
    }

    public async Task<(bool IsSuccess, string Log)> InstallSingBoxAsync(ISshService ssh, string targetVersion = "latest")
    {
        _logger.Log("DEPLOY-TRACE", $"[INSTALL] Умная установка Sing-box ({targetVersion})...");

        // Умный bash-скрипт с защитой от сбоев GitHub API (Rate Limits) и бага "vhttps"
        string installCmd = @"
# 1. Пытаемся получить актуальную версию без буквы 'v' (например, 1.8.11)
LATEST_VERSION=$(curl -sL https://api.github.com/repos/SagerNet/sing-box/releases/latest | grep '""tag_name"":' | sed -E 's/.*""v([^""]+)"".*/\1/')

# 2. Если GitHub API выдал ограничение (Rate Limit) или спарсился мусор
if [ -z ""$LATEST_VERSION"" ] || [[ ""$LATEST_VERSION"" == *""http""* ]]; then
    echo 'WARN: Ошибка парсинга GitHub API или лимит запросов. Используем стабильный Fallback.'
    LATEST_VERSION=""1.8.11"" # Стабильная резервная версия
fi

echo ""Установка Sing-box версии: $LATEST_VERSION""

# 3. Запускаем официальный скрипт, ЖЕСТКО передавая ему версию, чтобы обойти их баг
bash <(curl -fsSL https://sing-box.app/install.sh) install ""$LATEST_VERSION""

# 4. Проверяем физическое наличие бинарника после установки
if command -v sing-box >/dev/null 2>&1; then 
    echo 'SUCCESS_INSTALLED'
else 
    echo 'FAIL_INSTALL'
fi
".Replace("\r", ""); // Жестко вырезаем \r для безопасного выполнения в Linux

        string log = await ssh.ExecuteCommandAsync(installCmd, TimeSpan.FromMinutes(3));

        // Проверяем наш надежный маркер успеха
        bool isSuccess = log.Contains("SUCCESS_INSTALLED") || log.Contains("installed") || log.Contains("already");

        return (isSuccess, log);
    }

    public async Task<(bool IsSuccess, string Log)> InstallTrustTunnelAsync(ISshService ssh, string targetVersion = "latest")
    {
        _logger.Log("DEPLOY-TRACE", $"[INSTALL] Скачивание и установка официального TrustTunnel-core ({targetVersion})...");
        string installCmd = @"
wget -qO /tmp/trusttunnel.tar.gz https://github.com/TrustTunnel/TrustTunnel/releases/latest/download/trusttunnel-linux-amd64.tar.gz || \
wget -qO /usr/local/bin/trusttunnel https://github.com/TrustTunnel/TrustTunnel/releases/latest/download/trusttunnel-linux-amd64
if [ -f /tmp/trusttunnel.tar.gz ]; then
    tar -xzf /tmp/trusttunnel.tar.gz -C /tmp/
    mv /tmp/trusttunnel /usr/local/bin/trusttunnel
    rm /tmp/trusttunnel.tar.gz
fi
chmod +x /usr/local/bin/trusttunnel
if command -v trusttunnel >/dev/null 2>&1; then echo 'success'; else echo 'failed'; fi
".Replace("\r", "");
        string log = await ssh.ExecuteCommandAsync(installCmd, TimeSpan.FromMinutes(3));
        return (log.Contains("success"), log);
    }

    public async Task<(bool IsSuccess, string Log)> DeployFullStackAsync(
        ISshService ssh, VpnProfile profile, string coreType, List<(IProtocolBuilder Builder, int Port)> protocols)
    {
        try
        {
            _logger.Log("DEPLOY-TRACE", $"[1/7] Начало развертывания. Выбранное ядро: {coreType.ToUpper()}");

            _logger.Log("DEPLOY-TRACE", "[2/7] ТОТАЛЬНАЯ ЖЕСТКАЯ ЗАЧИСТКА ВСЕХ ЯДЕР И ПОРТОВ...");
            string hardWipeCmd = @"
systemctl stop sing-box xray trusttunnel 2>/dev/null || true
systemctl disable sing-box xray trusttunnel 2>/dev/null || true
killall -9 sing-box xray trusttunnel 2>/dev/null || true
rm -rf /usr/local/etc/xray /etc/sing-box /etc/trusttunnel
mkdir -p /etc/sing-box /usr/local/etc/xray /etc/trusttunnel
".Replace("\r", "");
            await ssh.ExecuteCommandAsync(hardWipeCmd);

            _logger.Log("DEPLOY-TRACE", "[3/7] Установка/Обновление ядра на сервере...");
            bool installSuccess = false;
            string installLog = "";

            if (coreType.ToLower() == "sing-box")
            {
                var res = await InstallSingBoxAsync(ssh); installSuccess = res.IsSuccess; installLog = res.Log;
            }
            else if (coreType.ToLower() == "trusttunnel")
            {
                var res = await InstallTrustTunnelAsync(ssh); installSuccess = res.IsSuccess; installLog = res.Log;
            }
            else
            {
                var res = await InstallXrayAsync(ssh); installSuccess = res.IsSuccess; installLog = res.Log;
            }

            if (!installSuccess)
            {
                _logger.Log("DEPLOY-ERROR", $"[FAIL] Провал установки ядра: {installLog}");
                return (false, $"Ошибка установки ядра: {installLog}");
            }

            _logger.Log("DEPLOY-TRACE", "[4/7] Настройка Firewall и умная генерация/восстановление криптографии...");

            var existingInbounds = profile.Inbounds.ToList();
            profile.Inbounds.Clear();
            var processedTypes = new HashSet<string>();

            foreach (var p in protocols)
            {
                _logger.Log("DEPLOY-TRACE", $"[PORT-CONFIG] Подготовка {p.Builder.ProtocolType.ToUpper()} на порту {p.Port}...");

                string fwCmd = $@"
if command -v ufw >/dev/null 2>&1; then ufw allow {p.Port}/tcp && ufw allow {p.Port}/udp || true; fi
if command -v firewall-cmd >/dev/null 2>&1; then firewall-cmd --add-port={p.Port}/tcp --permanent && firewall-cmd --add-port={p.Port}/udp --permanent && firewall-cmd --reload || true; fi
iptables -I INPUT 1 -p tcp --dport {p.Port} -j ACCEPT || true
iptables -I INPUT 1 -p udp --dport {p.Port} -j ACCEPT || true
iptables-save > /etc/iptables/rules.v4 2>/dev/null || true
".Replace("\r", "");
                await ssh.ExecuteCommandAsync(fwCmd);

                var existingDb = existingInbounds.FirstOrDefault(i => i.Protocol.ToLower() == p.Builder.ProtocolType.ToLower());
                ServerInbound inboundDb;

                if (existingDb != null && existingDb.Port == p.Port)
                {
                    _logger.Log("DEPLOY-TRACE", $"[PORT-CONFIG] Найдена конфигурация в БД. Запуск Смарт-восстановления для защиты юзеров...");

                    try
                    {
                        var settings = JsonNode.Parse(existingDb.SettingsJson);
                        string? certPath = settings?["certPath"]?.ToString();
                        string? keyPath = settings?["keyPath"]?.ToString();
                        string sni = settings?["sni"]?.ToString() ?? "bing.com";

                        // === УМНЫЙ АЛГОРИТМ ЗАЩИТЫ ===
                        // Если протокол использует файлы (Hysteria 2 / TrustTunnel), проверяем их наличие на диске.
                        if (!string.IsNullOrWhiteSpace(certPath) && !string.IsNullOrWhiteSpace(keyPath))
                        {
                            string restoreCertsCmd = $@"
if [ ! -f ""{certPath}"" ] || [ ! -f ""{keyPath}"" ]; then
    mkdir -p $(dirname ""{certPath}"")
    openssl ecparam -genkey -name prime256v1 -out ""{keyPath}""
    openssl req -new -x509 -days 36500 -key ""{keyPath}"" -out ""{certPath}"" -subj ""/CN={sni}""
    echo 'RESTORED'
fi
".Replace("\r", "");

                            string restoreResult = await ssh.ExecuteCommandAsync(restoreCertsCmd);
                            if (restoreResult.Contains("RESTORED"))
                            {
                                _logger.Log("DEPLOY-WARN", $"[PORT-CONFIG] Сертификаты отсутствовали на целевом сервере. Физические файлы успешно воссозданы без потери паролей юзеров!");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.Log("DEPLOY-ERROR", $"[PORT-CONFIG] Ошибка смарт-восстановления: {ex.Message}");
                    }

                    inboundDb = existingDb; // Строго сохраняем старый объект с ID и тегами!
                    _logger.Log("DEPLOY-TRACE", $"[PORT-CONFIG] Связи базы данных сохранены. Клиенты не потеряют доступ!");
                }
                else
                {
                    _logger.Log("DEPLOY-TRACE", $"[PORT-CONFIG] Генерация абсолютно НОВЫХ ключей для {p.Builder.ProtocolType.ToUpper()}...");
                    inboundDb = await p.Builder.GenerateNewInboundAsync(ssh, p.Port);
                }

                profile.Inbounds.Add(inboundDb);
                processedTypes.Add(inboundDb.Protocol.ToLower());
            }

            _logger.Log("DEPLOY-TRACE", "[5/7] Сборка и заливка базового конфига...");

            if (coreType.ToLower() == "trusttunnel")
            {
                var inbound = profile.Inbounds.FirstOrDefault(i => i.Protocol.ToLower() == "trusttunnel");
                if (inbound != null)
                {
                    var settings = JsonNode.Parse(inbound.SettingsJson);
                    string certPath = settings?["certPath"]?.ToString() ?? "";
                    string keyPath = settings?["keyPath"]?.ToString() ?? "";
                    string sni = settings?["sni"]?.ToString() ?? "vpn.trusttunnel.local";

                    string vpnToml = $@"
listen_address = ""0.0.0.0:{inbound.Port}""
ipv6_available = true
allow_private_network_connections = false
tls_handshake_timeout_secs = 10
client_listener_timeout_secs = 600
connection_establishment_timeout_secs = 30
tcp_connections_timeout_secs = 604800
udp_connections_timeout_secs = 300
credentials_file = ""credentials.toml""
rules_file = ""rules.toml""

[listen_protocols.http2]
initial_connection_window_size = 8388608
initial_stream_window_size = 131072
max_concurrent_streams = 1000

[listen_protocols.quic]
recv_udp_payload_size = 1350
send_udp_payload_size = 1350
initial_max_data = 104857600
initial_max_stream_data_bidi_local = 1048576
initial_max_stream_data_bidi_remote = 1048576
initial_max_streams_bidi = 4096
enable_early_data = true

[forward_protocol]
direct = {{}}
";
                    string hostsToml = $@"
[[main_hosts]]
hostname = ""{sni}""
cert_chain_path = ""{certPath}""
private_key_path = ""{keyPath}""

[[ping_hosts]]
hostname = ""ping.{sni}""
cert_chain_path = ""{certPath}""
private_key_path = ""{keyPath}""
";
                    string rulesToml = "[[rule]]\naction = \"allow\"\n";
                    string credsToml = "# Users will be added here by UserManager\n";

                    await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(vpnToml))}' | base64 -d > /etc/trusttunnel/vpn.toml");
                    await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(hostsToml))}' | base64 -d > /etc/trusttunnel/hosts.toml");
                    await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(rulesToml))}' | base64 -d > /etc/trusttunnel/rules.toml");
                    await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(credsToml))}' | base64 -d > /etc/trusttunnel/credentials.toml");

                    string serviceCmd = @"
cat > /etc/systemd/system/trusttunnel.service <<EOF
[Unit]
Description=TrustTunnel VPN Server
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=/etc/trusttunnel
ExecStart=/usr/local/bin/trusttunnel --config /etc/trusttunnel/vpn.toml
Restart=on-failure
RestartSec=5
LimitNOFILE=infinity

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
".Replace("\r", "");
                    await ssh.ExecuteCommandAsync(serviceCmd);
                }
            }
            else
            {
                var inboundsArray = new JsonArray();

                foreach (var inboundDb in profile.Inbounds)
                {
                    var settings = JsonNode.Parse(inboundDb.SettingsJson);
                    string protocol = inboundDb.Protocol.ToLower();

                    if (coreType.ToLower() == "sing-box")
                    {
                        if (protocol == "vless")
                        {
                            inboundsArray.Add(new JsonObject
                            {
                                ["type"] = "vless",
                                ["tag"] = inboundDb.Tag,
                                ["listen"] = "0.0.0.0",
                                ["listen_port"] = inboundDb.Port,
                                ["users"] = new JsonArray(),
                                ["tls"] = new JsonObject
                                {
                                    ["enabled"] = true,
                                    ["server_name"] = settings?["sni"]?.ToString(),
                                    ["reality"] = new JsonObject
                                    {
                                        ["enabled"] = true,
                                        ["handshake"] = new JsonObject { ["server"] = settings?["sni"]?.ToString(), ["server_port"] = 443 },
                                        ["private_key"] = settings?["privateKey"]?.ToString(),
                                        ["short_id"] = new JsonArray { settings?["shortId"]?.ToString() }
                                    }
                                }
                            });
                        }
                        else if (protocol == "hysteria2")
                        {
                            inboundsArray.Add(new JsonObject
                            {
                                ["type"] = "hysteria2",
                                ["tag"] = inboundDb.Tag,
                                ["listen"] = "0.0.0.0",
                                ["listen_port"] = inboundDb.Port,
                                ["users"] = new JsonArray(),
                                ["tls"] = new JsonObject
                                {
                                    ["enabled"] = true,
                                    ["alpn"] = new JsonArray { "h3" },
                                    ["certificate_path"] = settings?["certPath"]?.ToString(),
                                    ["key_path"] = settings?["keyPath"]?.ToString()
                                },
                                ["obfs"] = new JsonObject { ["type"] = "salamander", ["password"] = settings?["obfsPassword"]?.ToString() }
                            });
                        }
                    }
                    else
                    {
                        if (protocol == "vless")
                        {
                            inboundsArray.Add(new JsonObject
                            {
                                ["protocol"] = "vless",
                                ["listen"] = "0.0.0.0",
                                ["port"] = inboundDb.Port,
                                ["settings"] = new JsonObject { ["clients"] = new JsonArray(), ["decryption"] = "none" },
                                ["streamSettings"] = new JsonObject
                                {
                                    ["network"] = "tcp",
                                    ["security"] = "reality",
                                    ["realitySettings"] = new JsonObject
                                    {
                                        ["show"] = false,
                                        ["dest"] = $"{settings?["sni"]}:443",
                                        ["serverNames"] = new JsonArray { settings?["sni"]?.ToString() },
                                        ["privateKey"] = settings?["privateKey"]?.ToString(),
                                        ["shortIds"] = new JsonArray { settings?["shortId"]?.ToString() }
                                    }
                                }
                            });
                        }
                    }
                }

                var baseConfig = new JsonObject();

                if (coreType.ToLower() == "sing-box")
                {
                    baseConfig["log"] = new JsonObject { ["level"] = "info" };
                    baseConfig["inbounds"] = inboundsArray;
                    baseConfig["outbounds"] = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" }, new JsonObject { ["type"] = "block", ["tag"] = "block" } };
                    baseConfig["route"] = new JsonObject { ["rules"] = new JsonArray() };
                }
                else
                {
                    baseConfig["log"] = new JsonObject { ["loglevel"] = "warning" };
                    baseConfig["inbounds"] = inboundsArray;
                    baseConfig["outbounds"] = new JsonArray { new JsonObject { ["protocol"] = "freedom", ["tag"] = "direct" }, new JsonObject { ["protocol"] = "blackhole", ["tag"] = "block" } };
                    baseConfig["routing"] = new JsonObject { ["rules"] = new JsonArray() };
                }

                string configPath = coreType.ToLower() == "sing-box" ? "/etc/sing-box/config.json" : "/usr/local/etc/xray/config.json";
                string configStr = baseConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
                await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(configStr))}' | base64 -d > {configPath}");

                string fixServiceCmd = coreType.ToLower() == "sing-box" ? @"
BIN_PATH=$(command -v sing-box)
if [ -z ""$BIN_PATH"" ]; then BIN_PATH=""/usr/local/bin/sing-box""; fi
cat > /etc/systemd/system/sing-box.service <<EOF
[Unit]
Description=Sing-Box Service
After=network.target nss-lookup.target network-online.target

[Service]
User=root
CapabilityBoundingSet=CAP_NET_ADMIN CAP_NET_BIND_SERVICE CAP_NET_RAW
AmbientCapabilities=CAP_NET_ADMIN CAP_NET_BIND_SERVICE CAP_NET_RAW
ExecStart=$BIN_PATH run -c /etc/sing-box/config.json
Restart=on-failure
RestartSec=5
LimitNOFILE=infinity

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
".Replace("\r", "") : @"
BIN_PATH=$(command -v xray)
if [ -z ""$BIN_PATH"" ]; then BIN_PATH=""/usr/local/bin/xray""; fi
cat > /etc/systemd/system/xray.service <<EOF
[Unit]
Description=Xray Service
After=network.target nss-lookup.target network-online.target

[Service]
User=root
ExecStart=$BIN_PATH run -config /usr/local/etc/xray/config.json
Restart=on-failure
RestartSec=5
LimitNOFILE=infinity

[Install]
WantedBy=multi-user.target
EOF
systemctl daemon-reload
".Replace("\r", "");

                await ssh.ExecuteCommandAsync(fixServiceCmd);

                // Валидация перед запуском
                string checkCmd = coreType.ToLower() == "sing-box" ? $"sing-box check -c {configPath} 2>&1" : $"xray run -test-config {configPath} 2>&1";
                string checkResult = await ssh.ExecuteCommandAsync(checkCmd);

                if (checkResult.Contains("FATAL") || checkResult.Contains("error") || checkResult.Contains("failed"))
                {
                    throw new Exception($"Критическая ошибка синтаксиса JSON! Ядро отклонило конфиг:\n{checkResult.Trim()}");
                }
            }

            _logger.Log("DEPLOY-TRACE", "[6/7] Сохранение БД панели и перезапуск службы...");

            profile.CoreType = coreType.ToLower();
            _profileRepository.UpdateProfile(profile);

            string restartResult = await ssh.ExecuteCommandAsync($"systemctl enable {coreType.ToLower()} --now && systemctl restart {coreType.ToLower()}");

            _logger.Log("DEPLOY-TRACE", "[7/7] Сбор расширенной сетевой диагностики после запуска...");
            string diagCmd = $@"
sleep 2
echo '=== 1 ПРОВЕРКА ПРОСЛУШИВАНИЯ ПОРТОВ (SS) ==='
ss -tulpn | grep -E 'sing-box|xray|trusttunnel|:443|:8443|:4443' || true
echo '=== 2 СТАТУС СЛУЖБЫ ==='
systemctl status {coreType.ToLower()} -l --no-pager || true
".Replace("\r", "");

            string diagLog = await ssh.ExecuteCommandAsync(diagCmd);
            _logger.Log("SERVER-DIAGNOSTIC", $"\n{diagLog}");

            _logger.Log("DEPLOY-TRACE", $"[SUCCESS] Успешно развернуто! Ядро запущено: {coreType.ToUpper()}");
            return (true, $"Успешно развернуто! Ядро: {coreType.ToUpper()}.");
        }
        catch (Exception ex)
        {
            _logger.Log("DEPLOY-ERROR", $"[КРИТИЧЕСКАЯ ОШИБКА] {ex.Message}\n{ex.StackTrace}");
            return (false, ex.Message);
        }
    }
}