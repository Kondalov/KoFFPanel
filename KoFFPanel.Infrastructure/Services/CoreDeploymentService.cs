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

public partial class CoreDeploymentService : ICoreDeploymentService
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
        if (!ssh.IsConnected) return (false, "Нет SSH подключения");

        // === ИСПРАВЛЕНИЕ: Синхронизация времени (КРИТИЧНО ДЛЯ REALITY) и проверка прав ===
        string checkScript = @"
if [ ""$EUID"" -ne 0 ]; then 
    if ! command -v sudo >/dev/null 2>&1; then 
        echo 'ERROR|Пользователь не root, а утилита sudo не установлена. Деплой невозможен.'; exit 0; 
    fi
    if ! groups | grep -q -E '\bsudo\b|\bwheel\b'; then 
        echo 'ERROR|Пользователь не состоит в группе sudo или wheel. Нет прав для установки.'; exit 0; 
    fi
fi
if ! command -v systemctl >/dev/null 2>&1; then echo 'ERROR|Сервер не поддерживает systemd (требуется для служб).'; exit 0; fi
if ! command -v curl >/dev/null 2>&1; then echo 'ERROR|Пакет curl не установлен.'; exit 0; fi

# ЗАЩИТА ОТ ДУРАКА: Синхронизация времени. Рассинхрон > 2 минут вызывает глухой Тайм-аут в XTLS-Reality!
SUDO_CMD=""""
if [ ""$EUID"" -ne 0 ]; then SUDO_CMD=""sudo""; fi
$SUDO_CMD timedatectl set-ntp true 2>/dev/null || true
$SUDO_CMD apt-get update -q && $SUDO_CMD DEBIAN_FRONTEND=noninteractive apt-get install -y chrony tzdata >/dev/null 2>&1 || true
$SUDO_CMD systemctl restart chrony 2>/dev/null || true

echo 'READY|Сервер готов к установке.'
";
        string result = (await ssh.ExecuteCommandAsync(checkScript)).Trim();
        if (result.StartsWith("ERROR|")) return (false, result.Split('|')[1]);

        return (true, "Сервер готов.");
    }

    public async Task<string> GetInstalledXrayVersionAsync(ISshService ssh) => ssh.IsConnected ? (await ssh.ExecuteCommandAsync("xray version | head -n 1 | awk '{print $2}'")).Trim() : "Отключен";
    public async Task<string> GetInstalledSingBoxVersionAsync(ISshService ssh) => ssh.IsConnected ? (await ssh.ExecuteCommandAsync("sing-box version | grep 'version' | awk '{print $3}'")).Trim() : "Отключен";
    public async Task<string> GetInstalledTrustTunnelVersionAsync(ISshService ssh) => ssh.IsConnected ? (await ssh.ExecuteCommandAsync("/usr/local/bin/trusttunnel --version 2>/dev/null | awk '{print $2}'")).Trim() : "Отключен";

    public async Task<(bool IsSuccess, string Log)> InstallXrayAsync(ISshService ssh, string targetVersion = "latest")
        => await InstallXrayInternalAsync(ssh, targetVersion, "");

    public async Task<(bool IsSuccess, string Log)> InstallSingBoxAsync(ISshService ssh, string targetVersion = "latest")
        => await InstallSingBoxInternalAsync(ssh, targetVersion, "");

    public async Task<(bool IsSuccess, string Log)> InstallTrustTunnelAsync(ISshService ssh, string targetVersion = "latest")
        => await InstallTrustTunnelInternalAsync(ssh, targetVersion, "");

    public async Task<(bool IsSuccess, string Log)> DeployFullStackAsync(ISshService ssh, VpnProfile profile, string coreType, List<(IProtocolBuilder Builder, int Port, string? TtUsername, string? TtPassword)> protocols)
    {
        string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs", "deploy_steps.log");
        async Task LogStep(string step) 
        {
            string msg = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {step}";
            _logger.Log("DEPLOY-TRACE", msg);
            try { await File.AppendAllTextAsync(logPath, msg + "\n"); } catch { }
        }

        try
        {
            await LogStep($"=== НАЧАЛО ДЕПЛОЯ: Ядро {coreType}, IP {profile.IpAddress} ===");

            string sudoPrefix = profile.Username.Equals("root", StringComparison.OrdinalIgnoreCase)
                ? ""
                : $"echo '{profile.Password.Replace("'", "'\\''")}' | sudo -S ";

            string coreName = coreType.ToLower();

            await LogStep("[1/7] Подготовка файловой системы и остановка служб...");
            if (coreName == "trusttunnel")
            {
                await ssh.ExecuteCommandAsync($"{sudoPrefix}systemctl stop trusttunnel 2>/dev/null; {sudoPrefix}systemctl disable trusttunnel 2>/dev/null; {sudoPrefix}killall -9 trusttunnel trusttunnel_endpoint 2>/dev/null; {sudoPrefix}rm -rf /etc/trusttunnel /opt/trusttunnel /opt/trusttunnel2; {sudoPrefix}mkdir -p /opt/trusttunnel2");
                profile.Inbounds.RemoveAll(i => i.Protocol.ToLower() == "trusttunnel");
            }
            else
            {
                await ssh.ExecuteCommandAsync($"{sudoPrefix}systemctl stop sing-box xray 2>/dev/null; {sudoPrefix}systemctl disable sing-box xray 2>/dev/null; {sudoPrefix}killall -9 sing-box xray 2>/dev/null; {sudoPrefix}rm -rf /etc/sing-box /usr/local/etc/xray; {sudoPrefix}mkdir -p /etc/sing-box /usr/local/etc/xray");

                if (coreName == "xray")
                {
                    await ssh.ExecuteCommandAsync($"{sudoPrefix}mkdir -p /var/log/xray && {sudoPrefix}touch /var/log/xray/access.log /var/log/xray/error.log && {sudoPrefix}chmod -R 777 /var/log/xray");
                }

                // ИСПРАВЛЕНИЕ: При установке Xray/SingBox удаляем ВСЕ старые протоколы этих ядер. 
                // TrustTunnel удаляем только если в новом списке его нет.
                profile.Inbounds.RemoveAll(i => i.Protocol.ToLower() != "trusttunnel"); 
                
                // Если мы ставим только Xray/SingBox и не передали TrustTunnel в protocols, 
                // то логично и его вычистить для "чистой" установки, как просил пользователь.
                if (!protocols.Any(p => p.Builder.ProtocolType.ToLower() == "trusttunnel"))
                {
                    profile.Inbounds.RemoveAll(i => i.Protocol.ToLower() == "trusttunnel");
                }

                profile.CoreType = coreName;
            }

            await LogStep("[2/7] Установка бинарных файлов ядра...");
            var installRes = coreType.ToLower() == "sing-box" ? await InstallSingBoxInternalAsync(ssh, "latest", sudoPrefix) :
                             (coreType.ToLower() == "trusttunnel" ? await InstallTrustTunnelInternalAsync(ssh, "latest", sudoPrefix) :
                             await InstallXrayInternalAsync(ssh, "latest", sudoPrefix));

            if (!installRes.IsSuccess) 
            {
                await LogStep($"ОШИБКА: Не удалось установить ядро. Лог: {installRes.Log}");
                return (false, $"Ошибка установки ядра: {installRes.Log}");
            }

            await LogStep("[3/7] Генерация конфигураций для протоколов...");
            foreach (var p in protocols)
            {
                await LogStep($"Обработка протокола: {p.Builder.ProtocolType} на порту {p.Port}");
                var existingDb = profile.Inbounds.FirstOrDefault(i => i.Protocol.ToLower() == p.Builder.ProtocolType.ToLower());
                ServerInbound inboundDb;
                if (existingDb != null && existingDb.Port == p.Port) 
                { 
                    await LogStep("Восстановление существующих сертификатов...");
                    await SmartRestoreCertsAsync(ssh, existingDb); 
                    inboundDb = existingDb; 
                }
                else 
                {
                    await LogStep("Генерация новых ключей/сертификатов...");
                    inboundDb = await p.Builder.GenerateNewInboundAsync(ssh, p.Port);
                }
                
                if (!profile.Inbounds.Contains(inboundDb))
                    profile.Inbounds.Add(inboundDb);
            }

            await LogStep("[4/7] Настройка Firewall (открытие портов)...");
            foreach (var inbound in profile.Inbounds)
            {
                await LogStep($"Открытие порта {inbound.Port} ({inbound.Protocol})...");
                await SafeOpenPortAsync(ssh, inbound.Port, inbound.Protocol, sudoPrefix);
            }

            await LogStep("[5/7] Развертывание файлов конфигурации...");
            if (coreType.ToLower() == "trusttunnel") await DeployTrustTunnelConfigAsync(ssh, profile, sudoPrefix, protocols);
            else await DeployJsonCoreConfigAsync(ssh, profile, coreType.ToLower(), sudoPrefix);

            await LogStep("[6/7] Сохранение состояния в базу данных...");
            _profileRepository.UpdateProfile(profile);

            await LogStep("[7/7] Запуск службы и проверка статуса...");

            // ИСПРАВЛЕНИЕ: Жестко освобождаем порты перед запуском, так как зависшие UDP сокеты от старых процессов ядра часто вызывают 'bind: address already in use'
            foreach (var inbound in profile.Inbounds)
            {
                await LogStep($"Очистка возможных зависших процессов на порту {inbound.Port}...");
                string killCmd = $@"
                    if command -v fuser >/dev/null 2>&1; then {sudoPrefix}fuser -k -9 {inbound.Port}/tcp 2>/dev/null || true; {sudoPrefix}fuser -k -9 {inbound.Port}/udp 2>/dev/null || true; fi
                    if command -v lsof >/dev/null 2>&1; then PIDS=$({sudoPrefix}lsof -t -i:{inbound.Port} 2>/dev/null || true); if [ -n ""$PIDS"" ]; then {sudoPrefix}kill -9 $PIDS 2>/dev/null || true; fi; fi
                ";
                await ssh.ExecuteCommandAsync(killCmd);
            }

            await ssh.ExecuteCommandAsync($"{sudoPrefix}systemctl daemon-reload && {sudoPrefix}systemctl enable {coreName} --now && {sudoPrefix}systemctl restart {coreName}");
            
            // Даем время на запуск
            await Task.Delay(2000);
            string status = (await ssh.ExecuteCommandAsync($"systemctl is-active {coreName}")).Trim();

            if (status != "active")
            {
                string errorLogs = await ssh.ExecuteCommandAsync($"{sudoPrefix}journalctl -u {coreName} -n 50 --no-pager");
                await LogStep($"КРИТИЧЕСКИЙ СБОЙ: Сервис {coreName} имеет статус {status}!");
                await LogStep($"ЛОГИ ОШИБОК:\n{errorLogs}");
                return (false, $"Ядро {coreName} не запустилось! Статус: {status}. Логи:\n{errorLogs}");
            }

            await LogStep("=== ДЕПЛОЙ ЗАВЕРШЕН УСПЕШНО ===");
            return (true, $"Успешно развернуто! Ядро: {coreType.ToUpper()}.");
        }
        catch (Exception ex) 
        { 
            await LogStep($"КРИТИЧЕСКАЯ ОШИБКА ИСКЛЮЧЕНИЯ: {ex.Message}\n{ex.StackTrace}");
            return (false, ex.Message); 
        }
    }

    private async Task SafeOpenPortAsync(ISshService ssh, int port, string protocol, string sudoPrefix)
    {
        // ИСПРАВЛЕНИЕ: Жестко вставляем ACCEPT на ПЕРВУЮ позицию в INPUT, чтобы обойти любой UFW/Firewalld
        string cmd = $@"
if command -v ufw >/dev/null 2>&1; then {sudoPrefix}ufw allow {port}/tcp && {sudoPrefix}ufw allow {port}/udp || true; fi
if command -v firewall-cmd >/dev/null 2>&1; then {sudoPrefix}firewall-cmd --add-port={port}/tcp --permanent && {sudoPrefix}firewall-cmd --reload || true; fi
{sudoPrefix}iptables -I INPUT 1 -p tcp --dport {port} -j ACCEPT || true
{sudoPrefix}iptables -I INPUT 1 -p udp --dport {port} -j ACCEPT || true";

        cmd += $@"
{sudoPrefix}sh -c 'iptables-save > /etc/iptables/rules.v4' 2>/dev/null || true";

        await ssh.ExecuteCommandAsync(cmd);
    }

    private async Task<(bool IsSuccess, string Log)> InstallXrayInternalAsync(ISshService ssh, string targetVersion, string sudoPrefix)
    {
        string script = @"
apt-get update -q && apt-get install -y curl wget unzip jq >/dev/null 2>&1
DOWNLOAD_URL=$(curl -s https://api.github.com/repos/XTLS/Xray-core/releases/latest | jq -r "".assets[] | select(.name == \""Xray-linux-64.zip\"") | .browser_download_url"")
if [ -z ""$DOWNLOAD_URL"" ]; then echo 'FAIL_INSTALL'; exit 1; fi
rm -rf /tmp/xray_install && mkdir -p /tmp/xray_install && cd /tmp/xray_install
wget -q ""$DOWNLOAD_URL"" -O xray.zip
unzip -o xray.zip >/dev/null
chmod +x xray
mv xray /usr/local/bin/xray
if command -v xray >/dev/null 2>&1; then echo 'SUCCESS_INSTALLED'; else echo 'FAIL_INSTALL'; fi
cd /tmp && rm -rf /tmp/xray_install
".Replace("\r", "");

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        string log = await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d | {sudoPrefix}bash", TimeSpan.FromMinutes(3));
        return (log.Contains("SUCCESS_INSTALLED") || log.Contains("installed") || log.Contains("already"), log);
    }

    private async Task<(bool IsSuccess, string Log)> InstallSingBoxInternalAsync(ISshService ssh, string targetVersion, string sudoPrefix)
    {
        string script = @"
export DEBIAN_FRONTEND=noninteractive
# Делаем обновление некритичным, так как репозитории могут временно сбоить
apt-get update -q || true
apt-get install -y curl wget tar jq >/dev/null 2>&1 || true

ARCH=$(uname -m)
case ""$ARCH"" in
    x86_64) DL_ARCH=""amd64"" ;;
    aarch64) DL_ARCH=""arm64"" ;;
    *) echo ""FAIL_ARCH: $ARCH""; exit 1 ;;
esac

TAG=$(curl -sL --connect-timeout 5 https://api.github.com/repos/SagerNet/sing-box/releases/latest | jq -r "".tag_name"" 2>/dev/null)
if [ -z ""$TAG"" ] || [ ""$TAG"" == ""null"" ]; then TAG=""v1.13.11""; fi

DOWNLOAD_URL=""https://github.com/SagerNet/sing-box/releases/download/${TAG}/sing-box-${TAG#v}-linux-${DL_ARCH}.tar.gz""

rm -rf /tmp/singbox_install && mkdir -p /tmp/singbox_install && cd /tmp/singbox_install
if curl -sL --retry 3 --connect-timeout 10 ""$DOWNLOAD_URL"" -o sb.tar.gz; then
    if tar -xzf sb.tar.gz --strip-components=1; then
        chmod +x sing-box 2>/dev/null || true
        mv sing-box /usr/local/bin/sing-box
    fi
fi

if [ -f ""/usr/local/bin/sing-box"" ]; then
    # Дополнительно ставим xray как утилиту для статистики
    DOWNLOAD_URL_XRAY=$(curl -s https://api.github.com/repos/XTLS/Xray-core/releases/latest | jq -r "".assets[] | select(.name == \""Xray-linux-64.zip\"") | .browser_download_url"")
    if [ -n ""$DOWNLOAD_URL_XRAY"" ]; then
        wget -q ""$DOWNLOAD_URL_XRAY"" -O /tmp/xray_util.zip && unzip -o /tmp/xray_util.zip xray -d /usr/local/bin/ && chmod +x /usr/local/bin/xray
    fi
    /usr/local/bin/sing-box version > /dev/null 2>&1 && echo 'SUCCESS_INSTALLED' || echo 'FAIL_VERIFY'
else
    echo 'FAIL_INSTALL'
fi
cd /tmp && rm -rf /tmp/singbox_install
".Replace("\r", "");

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        string log = await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d | {sudoPrefix}bash", TimeSpan.FromMinutes(5));
        return (log.Contains("SUCCESS_INSTALLED"), log);
    }

    private async Task<(bool IsSuccess, string Log)> InstallTrustTunnelInternalAsync(ISshService ssh, string targetVersion, string sudoPrefix)
    {
        string script = @"
export DEBIAN_FRONTEND=noninteractive
apt-get update -q || true
apt-get install -y curl wget tar jq >/dev/null 2>&1 || true

DOWNLOAD_URL=$(curl -s https://api.github.com/repos/TrustTunnel/TrustTunnel/releases/latest | jq -r "".assets[].browser_download_url"" | grep -i ""linux"" | grep -E ""amd64|x86_64"" | grep ""tar.gz"" | grep -v ""dbgsym"" | grep -v ""debug"" | head -n 1)
if [ -z ""$DOWNLOAD_URL"" ]; then echo 'failed_download_url'; exit 1; fi

rm -rf /opt/trusttunnel2 && mkdir -p /opt/trusttunnel2 && cd /opt/trusttunnel2
if curl -sL --retry 3 --connect-timeout 10 ""$DOWNLOAD_URL"" -o tt.tar.gz; then
    if tar -xzf tt.tar.gz --strip-components=1; then
        chmod +x setup_wizard trusttunnel_endpoint 2>/dev/null || true
        rm tt.tar.gz
    fi
fi

if [ -f ""/opt/trusttunnel2/trusttunnel_endpoint"" ]; then 
    echo 'success'
else 
    echo 'failed_binary_missing'
fi
".Replace("\r", "");

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        string log = await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d | {sudoPrefix}bash", TimeSpan.FromMinutes(5));
        return (log.Contains("success"), log);
    }

    private async Task SmartRestoreCertsAsync(ISshService ssh, ServerInbound existingDb)
    {
        try {
            var settings = JsonNode.Parse(existingDb.SettingsJson);
            string? cp = settings?["certPath"]?.ToString(); string? kp = settings?["keyPath"]?.ToString();
            if (!string.IsNullOrWhiteSpace(cp) && !string.IsNullOrWhiteSpace(kp))
                await ssh.ExecuteCommandAsync($@"if [ ! -f ""{cp}"" ] || [ ! -f ""{kp}"" ]; then mkdir -p $(dirname ""{cp}""); openssl ecparam -genkey -name prime256v1 -out ""{kp}""; openssl req -new -x509 -days 36500 -key ""{kp}"" -out ""{cp}"" -subj ""/CN=google.com""; fi");
        } catch { }
    }

    private async Task DeployTrustTunnelConfigAsync(ISshService ssh, VpnProfile profile, string sudoPrefix, List<(IProtocolBuilder Builder, int Port, string? TtUsername, string? TtPassword)> protocols)
    {
        var inbound = profile.Inbounds.FirstOrDefault(i => i.Protocol.ToLower() == "trusttunnel");
        if (inbound == null) return;
        var settingsNode = JsonNode.Parse(inbound.SettingsJson) as JsonObject;
        string sni = settingsNode?["sni"]?.ToString() ?? "vpn.endpoint";

        // Получаем переданные креды
        var ttProtocolInfo = protocols.FirstOrDefault(p => p.Builder.ProtocolType.ToLower() == "trusttunnel");
        string username = string.IsNullOrWhiteSpace(ttProtocolInfo.TtUsername) ? "ADMIN" : ttProtocolInfo.TtUsername;
        string password = string.IsNullOrWhiteSpace(ttProtocolInfo.TtPassword) ? Guid.NewGuid().ToString("N").Substring(0, 16) : ttProtocolInfo.TtPassword;

        // ИСПРАВЛЕНИЕ: Сохраняем креды в SettingsJson для корректного отображения в окне доступа
        if (settingsNode != null)
        {
            settingsNode["username"] = username;
            settingsNode["password"] = password;
            inbound.SettingsJson = settingsNode.ToJsonString();
        }

        // Создаем или обновляем пользователя в БД для синхронизации
        using (var db = new KoFFPanel.Infrastructure.Data.AppDbContext())
        {
            var adminClient = db.Clients.FirstOrDefault(c => c.ServerIp == profile.IpAddress && c.Email == username);
            if (adminClient == null)
            {
                adminClient = new VpnClient 
                {
                    Email = username,
                    Uuid = password,
                    ServerIp = profile.IpAddress!,
                    IsTrustTunnelEnabled = true,
                    IsActive = true,
                    IsVlessEnabled = false,
                    IsHysteria2Enabled = false,
                    TrafficLimit = 0
                };
                db.Clients.Add(adminClient);
            }
            else
            {
                adminClient.Uuid = password;
                adminClient.IsTrustTunnelEnabled = true;
            }
            await db.SaveChangesAsync();
        }

        // Генерация ТОЧНЫХ и полных конфигураций через официальный мастер в неинтерактивном режиме (умная защита)
        await ssh.ExecuteCommandAsync($"{sudoPrefix}mkdir -p /opt/trusttunnel2");
        string setupCmd = $@"{sudoPrefix}cd /opt/trusttunnel2 && {sudoPrefix}./setup_wizard -m non-interactive -a 0.0.0.0:{inbound.Port} -c {username}:{password} -n {sni} --lib-settings vpn.toml --hosts-settings hosts.toml --cert-type self-signed";
        await ssh.ExecuteCommandAsync(setupCmd);

        string serviceData = @"[Unit]
Description=TrustTunnel VPN Service (Custom Port 5443)
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=/opt/trusttunnel2
ExecStart=/opt/trusttunnel2/trusttunnel_endpoint vpn.toml hosts.toml
Restart=on-failure
RestartSec=5
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target";

        await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(serviceData.Replace("\r", "")))}' | base64 -d | {sudoPrefix}tee /etc/systemd/system/trusttunnel.service > /dev/null");
        await ssh.ExecuteCommandAsync($"{sudoPrefix}systemctl daemon-reload");
    }

    private async Task DeployJsonCoreConfigAsync(ISshService ssh, VpnProfile profile, string core, string sudoPrefix)
    {
        var inboundsArray = new JsonArray();
        foreach (var inbound in profile.Inbounds)
        {
            var settings = JsonNode.Parse(inbound.SettingsJson);
            var node = core == "sing-box" ? BuildSingBoxInbound(inbound, settings) : BuildXrayInbound(inbound, settings);
            if (node != null) inboundsArray.Add(node);
        }

        var baseConfig = new JsonObject();
        if (core == "sing-box")
        {
            baseConfig["log"] = new JsonObject { ["level"] = "info" }; 
            baseConfig["inbounds"] = inboundsArray;
            baseConfig["outbounds"] = new JsonArray { new JsonObject { ["type"] = "direct", ["tag"] = "direct" }, new JsonObject { ["type"] = "block", ["tag"] = "block" } };
            baseConfig["route"] = new JsonObject { ["rules"] = new JsonArray() };
            baseConfig["experimental"] = new JsonObject 
            { 
                ["clash_api"] = new JsonObject 
                { 
                    ["external_controller"] = "127.0.0.1:9090",
                    ["secret"] = "" 
                }
            };
        }
        else
        {
            baseConfig["log"] = new JsonObject { ["loglevel"] = "warning", ["access"] = "/var/log/xray/access.log", ["error"] = "/var/log/xray/error.log" }; baseConfig["inbounds"] = inboundsArray;
            baseConfig["outbounds"] = new JsonArray { new JsonObject { ["protocol"] = "freedom", ["tag"] = "direct" }, new JsonObject { ["protocol"] = "blackhole", ["tag"] = "block" } };
            baseConfig["routing"] = new JsonObject { ["rules"] = new JsonArray() };
        }

        string path = core == "sing-box" ? "/etc/sing-box/config.json" : "/usr/local/etc/xray/config.json";
        await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(baseConfig.ToJsonString(new JsonSerializerOptions { WriteIndented = true })))}' | base64 -d | {sudoPrefix}tee {path} > /dev/null");

        string bin = core == "sing-box" ? "sing-box" : "xray";
        string exec = core == "sing-box" ? $"/usr/local/bin/sing-box run -c {path}" : $"/usr/local/bin/xray run -config {path}";

        string serviceData = $@"[Unit]
Description={bin} Service
After=network.target network-online.target

[Service]
User=root
CapabilityBoundingSet=CAP_NET_ADMIN CAP_NET_BIND_SERVICE
AmbientCapabilities=CAP_NET_ADMIN CAP_NET_BIND_SERVICE
ExecStart={exec}
ExecStopPost=/bin/sleep 2
Restart=on-failure
RestartSec=5
LimitNOFILE=infinity

[Install]
WantedBy=multi-user.target";

        await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(serviceData))}' | base64 -d | {sudoPrefix}tee /etc/systemd/system/{bin}.service > /dev/null");
        await ssh.ExecuteCommandAsync($"{sudoPrefix}systemctl daemon-reload");
    }
}