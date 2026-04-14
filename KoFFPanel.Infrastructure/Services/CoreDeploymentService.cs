using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
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
        _logger.Log("DEPLOY-TRACE", $"[INSTALL] Скачивание и установка Sing-box ({targetVersion})...");

        string log = await ssh.ExecuteCommandAsync("bash <(curl -fsSL https://sing-box.app/install.sh)", TimeSpan.FromMinutes(3));

        // ИСПРАВЛЕНИЕ: Переводим ответ Linux в нижний регистр для игнорирования больших/маленьких букв
        string lowerLog = log.ToLower();

        // ИСПРАВЛЕНИЕ: Умная проверка по всем возможным ключевым словам успешной установки/обновления
        bool isSuccess = lowerLog.Contains("installed") ||
                         lowerLog.Contains("success") ||
                         lowerLog.Contains("already") ||
                         lowerLog.Contains("setting up sing-box");

        return (isSuccess, log);
    }

    public async Task<(bool IsSuccess, string Log)> DeployFullStackAsync(
        ISshService ssh, VpnProfile profile, bool isSingBox, List<(IProtocolBuilder Builder, int Port)> protocols)
    {
        try
        {
            string coreName = isSingBox ? "sing-box" : "xray";
            _logger.Log("DEPLOY-TRACE", $"[1/7] Начало развертывания. Выбранное ядро: {coreName.ToUpper()}");

            _logger.Log("DEPLOY-TRACE", "[2/7] Остановка текущих служб и безопасная очистка конфигов...");
            string disableOldCmd = isSingBox ? "systemctl disable xray --now 2>/dev/null || true" : "systemctl disable sing-box --now 2>/dev/null || true";
            await ssh.ExecuteCommandAsync(disableOldCmd);

            string safeCleanup = @"
                systemctl stop sing-box xray 2>/dev/null || true
                killall -9 sing-box xray 2>/dev/null || true
                rm -f /usr/local/etc/xray/config.json /etc/sing-box/config.json
                mkdir -p /etc/sing-box /usr/local/etc/xray
            ".Replace("\r", "");
            await ssh.ExecuteCommandAsync(safeCleanup);

            _logger.Log("DEPLOY-TRACE", "[3/7] Установка/Обновление ядра на сервере...");

            _logger.Log("DEPLOY-TRACE", $"[3.1/7] Запуск официального скрипта установки (Таймаут 3 минуты!)...");
            var installResult = isSingBox ? await InstallSingBoxAsync(ssh) : await InstallXrayAsync(ssh);
            if (!installResult.IsSuccess)
            {
                _logger.Log("DEPLOY-ERROR", $"[FAIL] Провал установки ядра: {installResult.Log}");
                return (false, $"Ошибка установки ядра: {installResult.Log}");
            }

            _logger.Log("DEPLOY-TRACE", "[3.2/7] Принудительная генерация systemd службы (Защита от 203/EXEC)...");
            string fixServiceCmd = isSingBox ? @"
                BIN_PATH=$(command -v sing-box)
                if [ -z ""$BIN_PATH"" ]; then BIN_PATH=""/usr/local/bin/sing-box""; fi
                chmod +x $BIN_PATH
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
                chmod +x $BIN_PATH
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

            _logger.Log("DEPLOY-TRACE", "[3.3/7] Создание системных симлинков для совместимости путей...");
            string symlinkCmd = @"
                if command -v sing-box >/dev/null 2>&1; then 
                    SB_P=$(command -v sing-box)
                    if [ ""$SB_P"" != ""/usr/local/bin/sing-box"" ]; then ln -sf $SB_P /usr/local/bin/sing-box; fi
                fi
                if command -v xray >/dev/null 2>&1; then 
                    XR_P=$(command -v xray)
                    if [ ""$XR_P"" != ""/usr/local/bin/xray"" ]; then ln -sf $XR_P /usr/local/bin/xray; fi
                fi
            ".Replace("\r", "");
            await ssh.ExecuteCommandAsync(symlinkCmd);

            _logger.Log("DEPLOY-TRACE", "[4/7] Настройка Firewall и генерация криптографии для Inbounds...");

            var existingInbounds = profile.Inbounds.ToList();
            profile.Inbounds.Clear();
            var processedTypes = new HashSet<string>();

            foreach (var p in protocols)
            {
                _logger.Log("DEPLOY-TRACE", $"[PORT-CONFIG] Установка/Обновление {p.Builder.ProtocolType.ToUpper()} на порту {p.Port}...");
                string fwCmd = $@"
                    if command -v ufw >/dev/null 2>&1; then ufw allow {p.Port}/{p.Builder.TransportType} || true; fi
                    if command -v firewall-cmd >/dev/null 2>&1; then firewall-cmd --add-port={p.Port}/{p.Builder.TransportType} --permanent && firewall-cmd --reload || true; fi
                    iptables -I INPUT 1 -p {p.Builder.TransportType} --dport {p.Port} -j ACCEPT || true
                    iptables-save > /etc/iptables/rules.v4 2>/dev/null || true
                ".Replace("\r", "");
                await ssh.ExecuteCommandAsync(fwCmd);

                var existingDb = existingInbounds.FirstOrDefault(i => i.Protocol.ToLower() == p.Builder.ProtocolType.ToLower());
                ServerInbound inboundDb;

                if (existingDb != null)
                {
                    _logger.Log("DEPLOY-TRACE", $"[PORT-CONFIG] Найдена рабочая конфигурация {p.Builder.ProtocolType.ToUpper()}! Ключи восстановлены из БД.");
                    inboundDb = existingDb;
                    inboundDb.Port = p.Port;
                }
                else
                {
                    _logger.Log("DEPLOY-TRACE", $"[PORT-CONFIG] Генерация НОВЫХ ключей для {p.Builder.ProtocolType.ToUpper()}...");
                    inboundDb = await p.Builder.GenerateNewInboundAsync(ssh, p.Port);
                }

                profile.Inbounds.Add(inboundDb);
                processedTypes.Add(inboundDb.Protocol.ToLower());
            }

            // 4.2 Сохраняем нетронутых "Боссов", которые были в БД, но юзер их не отметил
            foreach (var existing in existingInbounds)
            {
                if (!processedTypes.Contains(existing.Protocol.ToLower()))
                {
                    string transport = existing.Protocol.ToLower() == "vless" ? "tcp" : "udp";

                    // ИСПРАВЛЕНИЕ: Проверяем, не перехватил ли НОВЫЙ протокол этот порт и транспорт (Алгоритм замены)
                    bool isPortStolen = profile.Inbounds.Any(newInbound =>
                        newInbound.Port == existing.Port &&
                        (newInbound.Protocol.ToLower() == "vless" ? "tcp" : "udp") == transport);

                    if (isPortStolen)
                    {
                        _logger.Log("DEPLOY-TRACE", $"[REPLACE] Протокол {existing.Protocol.ToUpper()} УДАЛЕН! Порт {existing.Port} ({transport.ToUpper()}) передан новому протоколу.");
                        continue; // Пропускаем сохранение, старый протокол стирается!
                    }

                    _logger.Log("DEPLOY-TRACE", $"[PRESERVE] Сохранение нетронутого протокола {existing.Protocol.ToUpper()} на порту {existing.Port}...");
                    string fwCmd = $@"
                        if command -v ufw >/dev/null 2>&1; then ufw allow {existing.Port}/{transport} || true; fi
                        if command -v firewall-cmd >/dev/null 2>&1; then firewall-cmd --add-port={existing.Port}/{transport} --permanent && firewall-cmd --reload || true; fi
                        iptables -I INPUT 1 -p {transport} --dport {existing.Port} -j ACCEPT || true
                        iptables-save > /etc/iptables/rules.v4 2>/dev/null || true
                    ".Replace("\r", "");
                    await ssh.ExecuteCommandAsync(fwCmd);

                    profile.Inbounds.Add(existing);
                }
            }

            _logger.Log("DEPLOY-TRACE", "[5/7] Сборка и заливка базового JSON конфига...");
            var inboundsArray = new JsonArray();

            foreach (var inboundDb in profile.Inbounds)
            {
                var settings = JsonNode.Parse(inboundDb.SettingsJson);
                string protocol = inboundDb.Protocol.ToLower();

                if (isSingBox)
                {
                    if (protocol == "vless" && settings?["transport"]?["type"]?.ToString() != "quic")
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
                    else if (protocol == "trusttunnel" || (protocol == "vless" && settings?["transport"]?["type"]?.ToString() == "quic"))
                    {
                        inboundsArray.Add(new JsonObject
                        {
                            ["type"] = "vless",
                            ["tag"] = inboundDb.Tag,
                            ["listen"] = "0.0.0.0",
                            ["listen_port"] = inboundDb.Port,
                            ["users"] = new JsonArray(),
                            ["transport"] = new JsonObject { ["type"] = "quic" },
                            ["tls"] = new JsonObject
                            {
                                ["enabled"] = true,
                                ["server_name"] = settings?["sni"]?.ToString(),
                                ["alpn"] = new JsonArray { "h3" },
                                ["certificate_path"] = settings?["certPath"]?.ToString(),
                                ["key_path"] = settings?["keyPath"]?.ToString()
                            }
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
                            ["port"] = inboundDb.Port,
                            ["listen"] = "0.0.0.0",
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
            if (isSingBox)
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

            string configStr = baseConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            string base64Config = Convert.ToBase64String(Encoding.UTF8.GetBytes(configStr));
            string configPath = isSingBox ? "/etc/sing-box/config.json" : "/usr/local/etc/xray/config.json";

            await ssh.ExecuteCommandAsync($"echo '{base64Config}' | base64 -d > {configPath}");

            _logger.Log("DEPLOY-TRACE", "[5.5/7] Валидация сгенерированного конфига...");
            string checkCmd = isSingBox ? $"sing-box check -c {configPath} 2>&1" : $"xray run -test -config {configPath} 2>&1";
            string checkResult = await ssh.ExecuteCommandAsync(checkCmd);

            if (checkResult.Contains("FATAL") || checkResult.Contains("error") || checkResult.Contains("failed"))
            {
                throw new Exception($"Критическая ошибка синтаксиса JSON! Ядро отклонило конфиг:\n{checkResult.Trim()}");
            }

            _logger.Log("DEPLOY-TRACE", "[6/7] Сохранение БД панели и перезапуск ядра...");
            profile.CoreType = coreName;
            _profileRepository.UpdateProfile(profile);

            // ИСПРАВЛЕНИЕ: Гарантированно убиваем зомби-процессы прямо перед рестартом службы!
            string preRestartCleanup = @"
                systemctl stop sing-box xray 2>/dev/null || true
                killall -9 sing-box xray 2>/dev/null || true
            ".Replace("\r", "");
            await ssh.ExecuteCommandAsync(preRestartCleanup);

            string restartResult = await ssh.ExecuteCommandAsync($"systemctl enable {coreName} --now && systemctl restart {coreName}");

            _logger.Log("DEPLOY-TRACE", "[7/7] Сбор расширенной сетевой диагностики после запуска...");
            string diagCmd = @"
                sleep 2
                echo '=== 1. ПРОВЕРКА ПРОСЛУШИВАНИЯ ПОРТОВ (SS) ==='
                ss -tulpn | grep -E 'sing-box|xray|:443|:8443' || true
                echo '=== 2. СТАТУС СЛУЖБЫ ==='
                systemctl status sing-box -l --no-pager || true
            ".Replace("\r", "");
            string diagLog = await ssh.ExecuteCommandAsync(diagCmd);
            _logger.Log("SERVER-DIAGNOSTIC", $"\n{diagLog}");

            _logger.Log("DEPLOY-TRACE", $"[SUCCESS] Успешно развернуто! Ядро запущено.");
            return (true, $"Успешно развернуто! Ядро: {coreName}.");
        }
        catch (Exception ex)
        {
            _logger.Log("DEPLOY-ERROR", $"[КРИТИЧЕСКАЯ ОШИБКА] {ex.Message}\n{ex.StackTrace}");
            return (false, ex.Message);
        }
    }
}