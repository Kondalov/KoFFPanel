using KoFFPanel.Application.Interfaces;
using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public partial class CoreDeploymentService : ICoreDeploymentService
{
    private readonly IAppLogger _logger;
    private readonly IProfileRepository _profileRepository;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IAcmeCertificateService? _acmeService;
    private readonly ISingBoxConfiguratorService? _singBoxConfigurator;

    public CoreDeploymentService(
        IAppLogger logger, 
        IProfileRepository profileRepository, 
        ISubscriptionService subscriptionService,
        IAcmeCertificateService? acmeService = null,
        ISingBoxConfiguratorService? singBoxConfigurator = null)
    {
        _logger = logger;
        _profileRepository = profileRepository;
        _subscriptionService = subscriptionService;
        _acmeService = acmeService;
        _singBoxConfigurator = singBoxConfigurator;
    }

    public async Task<(bool IsSuccess, string Message)> RunPreFlightChecksAsync(ISshService ssh)
    {
        if (!ssh.IsConnected) return (false, "Нет SSH подключения");

        // === ИСПРАВЛЕНИЕ: Синхронизация времени и установка утилит для очистки портов ===
        string checkScript = @"
if [ ""$EUID"" -ne 0 ]; then 
    if ! command -v sudo >/dev/null 2>&1; then 
        echo 'ERROR|Пользователь не root, а утилита sudo не установлена. Деплой невозможен.'; exit 0; 
    fi
    if ! groups | grep -q -E '\bsudo\b|\bwheel\b'; then 
        echo 'ERROR|Пользователь не состоит в группе sudo или wheel. Нет прав для установки.'; exit 0; 
    fi
fi
if ! command -v systemctl >/dev/null 2>&1; then echo 'ERROR|Сервер не поддерживает systemd.'; exit 0; fi

SUDO_CMD=""""
if [ ""$EUID"" -ne 0 ]; then SUDO_CMD=""sudo""; fi

# Установка необходимых утилит (psmisc для fuser, lsof для очистки портов)
$SUDO_CMD apt-get update -q && $SUDO_CMD DEBIAN_FRONTEND=noninteractive apt-get install -y curl wget unzip jq psmisc lsof chrony tzdata >/dev/null 2>&1 || true

# Синхронизация времени
$SUDO_CMD timedatectl set-ntp true 2>/dev/null || true
$SUDO_CMD systemctl restart chrony 2>/dev/null || true

echo 'READY|Сервер готов к установке.'
";
        string result = (await ssh.ExecuteCommandAsync(checkScript)).Trim();
        if (result.StartsWith("ERROR|")) return (false, result.Split('|')[1]);

        return (true, "Сервер готов.");
    }

    public async Task<string> GetInstalledXrayVersionAsync(ISshService ssh) => ssh.IsConnected ? (await ssh.ExecuteCommandAsync("xray version | head -n 1 | awk '{print $2}'")).Trim() : "Отключен";
    public async Task<string> GetInstalledSingBoxVersionAsync(ISshService ssh) => ssh.IsConnected ? (await ssh.ExecuteCommandAsync("sing-box version | grep 'version' | awk '{print $3}'")).Trim() : "Отключен";

    public async Task<(bool IsSuccess, string Log)> InstallXrayAsync(ISshService ssh, string targetVersion = "latest")
        => await InstallXrayInternalAsync(ssh, targetVersion, "");

    public async Task<(bool IsSuccess, string Log)> InstallSingBoxAsync(ISshService ssh, string targetVersion = "latest")
        => await InstallSingBoxInternalAsync(ssh, targetVersion, "");

    public async Task<(bool IsSuccess, string Log)> DeployFullStackAsync(ISshService ssh, VpnProfile profile, string coreType, List<(IProtocolBuilder Builder, int Port)> protocols)
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
                : "sudo ";

            string coreName = coreType.ToLower();

            await LogStep("[1/7] Подготовка файловой системы и остановка служб (Smart Cleanup)...");
            _logger.Log("DEPLOY-DEBUG", $"Текущий домен подписки: {(string.IsNullOrEmpty(profile.CustomDomain) ? "НЕ ЗАДАН (будет HTTP)" : profile.CustomDomain)}");

            string cleanupCmd = $@"
            {sudoPrefix}systemctl stop sing-box xray 2>/dev/null || true
            {sudoPrefix}systemctl disable sing-box xray 2>/dev/null || true
            {sudoPrefix}pkill -9 sing-box 2>/dev/null || true
            {sudoPrefix}pkill -9 xray 2>/dev/null || true
            
            if command -v docker >/dev/null 2>&1; then
                {sudoPrefix}docker ps -q --filter ""name=sing-box"" --filter ""name=xray"" --filter ""name=koff-"" | xargs -r {sudoPrefix}docker stop 2>/dev/null || true
                {sudoPrefix}docker ps -aq --filter ""name=sing-box"" --filter ""name=xray"" --filter ""name=koff-"" | xargs -r {sudoPrefix}docker rm 2>/dev/null || true
            fi

            {sudoPrefix}rm -rf /etc/sing-box /usr/local/etc/xray /etc/koff
            {sudoPrefix}mkdir -p /etc/sing-box /usr/local/etc/xray /var/log/sing-box /etc/koff && {sudoPrefix}chmod 750 /var/log/sing-box
            sleep 1
        ";
            await ssh.ExecuteSudoCommandAsync(cleanupCmd, profile.Password, TimeSpan.FromSeconds(60));

            if (string.Equals(coreName, "xray", StringComparison.OrdinalIgnoreCase))
            {
                await ssh.ExecuteSudoCommandAsync($"mkdir -p /var/log/xray && touch /var/log/xray/access.log /var/log/xray/error.log && chmod -R 750 /var/log/xray", profile.Password);
            }

            profile.CoreType = coreName;

            await LogStep("[2/7] Установка бинарных файлов ядра...");
            var installRes = string.Equals(coreType, "sing-box", StringComparison.OrdinalIgnoreCase)
                ? await InstallSingBoxInternalAsync(ssh, "latest", sudoPrefix)
                : await InstallXrayInternalAsync(ssh, "latest", sudoPrefix);

            if (!installRes.IsSuccess)
            {
                await LogStep($"ОШИБКА: Не удалось установить ядро. Лог: {installRes.Log}");
                return (false, $"Ошибка установки ядра: {installRes.Log}");
            }

            await LogStep("[3/7] Генерация конфигураций для протоколов...");
            foreach (var p in protocols)
            {
                await LogStep($"Обработка протокола: {p.Builder.ProtocolType} на порту {p.Port}");
                var existingDb = profile.Inbounds.FirstOrDefault(i => string.Equals(i.Protocol, p.Builder.ProtocolType, StringComparison.OrdinalIgnoreCase));
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

                // Удаляем все устаревшие inbound'ы того же протокола или того же транспорта на том же порту (иначе возникает конфликт портов в конфиге ядра)
                string pTransport = GetTransport(p.Builder.ProtocolType);
                profile.Inbounds.RemoveAll(i => 
                    (string.Equals(i.Protocol, p.Builder.ProtocolType, StringComparison.OrdinalIgnoreCase) || 
                     (i.Port == p.Port && string.Equals(GetTransport(i.Protocol), pTransport, StringComparison.OrdinalIgnoreCase))) 
                    && !ReferenceEquals(i, inboundDb));

                if (!profile.Inbounds.Contains(inboundDb))
                    profile.Inbounds.Add(inboundDb);
            }

            if (!string.IsNullOrWhiteSpace(profile.CustomDomain) && _acmeService != null)
            {
                await LogStep($"[ACME] Проверка и подготовка SSL-сертификата Let's Encrypt для {profile.CustomDomain}...");
                var acmeRes = await _acmeService.EnsureCertificateAsync(ssh, profile.IpAddress, profile.CustomDomain);
                await LogStep($"[ACME] {acmeRes.Message}");
            }

            await LogStep("[4/7] Настройка Firewall и Threat Shield...");
            await LogStep("Активация Threat Shield (превентивная блокировка Censys/Shodan/Shadowserver)...");
            await ConfigureThreatShieldAsync(ssh, sudoPrefix);

            foreach (var inbound in profile.Inbounds)
            {
                await LogStep($"Открытие порта {inbound.Port} ({inbound.Protocol})...");
                await SafeOpenPortAsync(ssh, inbound.Port, inbound.Protocol, sudoPrefix);
            }

            await LogStep("[5/7] Развертывание файлов конфигурации...");
            if (string.Equals(coreType, "sing-box", StringComparison.OrdinalIgnoreCase) && _singBoxConfigurator != null)
            {
                await LogStep("Сборка и развертывание бинарных SRS правил маршрутизации (Sing-box)...");
                await _singBoxConfigurator.CompileAndDeployRuleSetsAsync(ssh);
            }

            await DeployJsonCoreConfigAsync(ssh, profile, coreType.ToLower(), sudoPrefix);

            await LogStep("[6/7] Настройка микросервиса подписок (HTTPS Ready)...");
            _subscriptionService.SetCustomDomain(profile.CustomDomain ?? string.Empty);
            await _subscriptionService.InitializeServerAsync(ssh);
            _profileRepository.UpdateProfile(profile);

            await LogStep("[7/7] Запуск службы и проверка статуса...");

            // === УМНЫЙ АЛГОРИТМ (FOOLPROOF): Изолированная очистка портов ===
            // Выбираем ТОЛЬКО те порты, которые мы реально устанавливаем прямо сейчас.
            // Это гарантирует, что мы не убьем соседние живые ядра (SingBox/Xray).
            var deployingPorts = protocols.Select(p => p.Port).Distinct().ToList();

            foreach (var port in deployingPorts)
            {
                await LogStep($"Точечная очистка процессов на целевом порту {port}...");
                string killCmd = $@"
            {sudoPrefix}fuser -k -9 {port}/tcp 2>/dev/null || true
            {sudoPrefix}fuser -k -9 {port}/udp 2>/dev/null || true
            PIDS=$({sudoPrefix}lsof -t -i:{port} 2>/dev/null || true)
            if [ -n ""$PIDS"" ]; then {sudoPrefix}kill -9 $PIDS 2>/dev/null || true; fi
        ";
                await ssh.ExecuteCommandAsync(killCmd, TimeSpan.FromSeconds(30));
            }

            // === УМНЫЙ АЛГОРИТМ: Изолированная валидация конфигов ===
            string checkCmd = coreName == "sing-box"
                ? $"{sudoPrefix}mkdir -p /var/log/sing-box && {sudoPrefix}sing-box check -c /etc/sing-box/config.json 2>&1"
                : $"{sudoPrefix}xray run -test -config /usr/local/etc/xray/config.json 2>&1";

            var checkRes = await ssh.ExecuteCommandAsync(checkCmd);

            if (checkRes.ToLower().Contains("error") || checkRes.ToLower().Contains("fatal"))
            {
                await LogStep("КРИТИЧЕСКАЯ ОШИБКА: Сгенерированный конфиг невалиден! Откат...");
                if (coreName == "sing-box")
                {
                    await ssh.ExecuteCommandAsync($"{sudoPrefix}cp /etc/sing-box/config.backup.json /etc/sing-box/config.json 2>/dev/null || true");
                }
                else if (coreName == "xray")
                {
                    await ssh.ExecuteCommandAsync($"{sudoPrefix}cp /usr/local/etc/xray/config.backup.json /usr/local/etc/xray/config.json 2>/dev/null || true");
                }
                return (false, "Ошибка валидации конфига. Система откачена к рабочему состоянию.");
            }

            // Перезагрузка служб
            await ssh.ExecuteCommandAsync($"{sudoPrefix}systemctl daemon-reload && {sudoPrefix}systemctl enable {coreName} --now && {sudoPrefix}systemctl restart {coreName}");

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

    private async Task ConfigureThreatShieldAsync(ISshService ssh, string sudoPrefix)
    {
        string script = @"
if ! iptables -L KOFF_SHIELD -n >/dev/null 2>&1; then
    iptables -N KOFF_SHIELD
fi

iptables -F KOFF_SHIELD

if ! iptables -C INPUT -j KOFF_SHIELD >/dev/null 2>&1; then
    iptables -I INPUT 1 -j KOFF_SHIELD
fi

SCANNERS=(
    '162.142.125.0/24'
    '167.94.138.0/24'
    '167.94.145.0/24'
    '167.94.146.0/24'
    '167.248.133.0/24'
    '192.35.169.0/23'
    '206.168.32.0/21'
    '66.240.205.34/32'
    '71.6.135.131/32'
    '71.6.165.200/32'
    '71.6.167.142/32'
    '82.221.105.6/32'
    '82.221.105.7/32'
    '85.25.43.94/32'
    '98.143.148.107/32'
    '185.180.143.0/24'
    '198.20.69.74/32'
    '198.20.70.114/32'
    '198.20.87.98/32'
    '198.20.99.130/32'
    '208.180.20.97/32'
    '209.126.110.38/32'
    '216.117.2.180/32'
    '216.218.206.0/24'
    '184.105.139.0/24'
    '184.105.247.0/24'
)

for net in ""${SCANNERS[@]}""; do
    iptables -A KOFF_SHIELD -s ""$net"" -j DROP
done

mkdir -p /etc/iptables
iptables-save > /etc/iptables/rules.v4 2>/dev/null || true
".Replace("\r", "");

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d | {sudoPrefix}bash");
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
apt-get update -q || true
apt-get install -y curl wget tar jq >/dev/null 2>&1 || true

ARCH=$(uname -m)
case ""$ARCH"" in
    x86_64) DL_ARCH=""amd64"" ;;
    aarch64) DL_ARCH=""arm64"" ;;
    armv7l|armv7) DL_ARCH=""armv7"" ;;
    *) echo ""FAIL_ARCH: $ARCH""; exit 1 ;;
esac

TAG=$(curl -sL --connect-timeout 5 https://api.github.com/repos/SagerNet/sing-box/releases/latest | jq -r "".tag_name"" 2>/dev/null)
if [ -z ""$TAG"" ] || [ ""$TAG"" == ""null"" ]; then
    TAG=$(curl -sIL -o /dev/null -w '%{url_effective}' https://github.com/SagerNet/sing-box/releases/latest 2>/dev/null | grep -o '[^/]*$')
fi
if [ -z ""$TAG"" ] || [ ""$TAG"" == ""null"" ] || [ ""$TAG"" == ""latest"" ]; then TAG=""v1.14.1""; fi

DOWNLOAD_URL=""https://github.com/SagerNet/sing-box/releases/download/${TAG}/sing-box-${TAG#v}-linux-${DL_ARCH}.tar.gz""

rm -rf /tmp/singbox_install && mkdir -p /tmp/singbox_install && cd /tmp/singbox_install
if curl -sL --retry 3 --connect-timeout 10 ""$DOWNLOAD_URL"" -o sb.tar.gz; then
    if tar -xzf sb.tar.gz --strip-components=1; then
        chmod +x sing-box 2>/dev/null || true
        mv -f sing-box /usr/local/bin/sing-box
        chmod +x /usr/local/bin/sing-box
    fi
fi

if [ -f ""/usr/local/bin/sing-box"" ]; then
    chmod +x /usr/local/bin/sing-box
    /usr/local/bin/sing-box version > /dev/null 2>&1 && echo 'SUCCESS_INSTALLED' || echo 'FAIL_VERIFY'
else
    echo 'FAIL_INSTALL'
fi
cd /tmp && rm -rf /tmp/singbox_install
".Replace("\r", "");

        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script));
        string log = await ssh.ExecuteCommandAsync($"echo '{b64}' | base64 -d | {sudoPrefix}bash", TimeSpan.FromMinutes(5));
        return (log.Contains("SUCCESS_INSTALLED") || log.Contains("installed") || log.Contains("already"), log);
    }

    private async Task SmartRestoreCertsAsync(ISshService ssh, ServerInbound existingDb)
    {
        try {
            var settings = JsonNode.Parse(existingDb.SettingsJson);
            string? cp = settings?["certPath"]?.ToString(); string? kp = settings?["keyPath"]?.ToString();
            if (!string.IsNullOrWhiteSpace(cp) && !string.IsNullOrWhiteSpace(kp))
                await ssh.ExecuteCommandAsync($@"if [ ! -f ""{cp}"" ] || [ ! -f ""{kp}"" ]; then mkdir -p $(dirname ""{cp}""); openssl ecparam -genkey -name prime256v1 -out ""{kp}""; openssl req -new -x509 -days 90 -key ""{kp}"" -out ""{cp}"" -subj ""/CN=vpn.local""; fi");
        } catch { }
    }

    private async Task DeployJsonCoreConfigAsync(ISshService ssh, VpnProfile profile, string core, string sudoPrefix)
    {
        // Дедуплицируем по (Порт, Транспорт): TCP и UDP на одном номере порта (например, 443 TCP VLESS и 443 UDP Hysteria2) независимы и не конфликтуют
        var uniqueInbounds = profile.Inbounds
            .GroupBy(i => (i.Port, GetTransport(i.Protocol)))
            .Select(g => g.Last())
            .ToList();

        var inboundsArray = new JsonArray();
        foreach (var inbound in uniqueInbounds)
        {
            var settings = JsonNode.Parse(inbound.SettingsJson);
            var node = core == "sing-box" ? BuildSingBoxInbound(inbound, settings) : BuildXrayInbound(inbound, settings);
            if (node != null) inboundsArray.Add(node);
        }

        var baseConfig = new JsonObject();
        if (core == "sing-box")
        {
            baseConfig["log"] = new JsonObject 
            { 
                ["disabled"] = false,
                ["level"] = "info", 
                ["timestamp"] = true,
                ["output"] = "/var/log/sing-box/access.log"
            }; 
            baseConfig["inbounds"] = inboundsArray;
            baseConfig["outbounds"] = new JsonArray 
            { 
                new JsonObject { ["type"] = "direct", ["tag"] = "direct" }, 
                new JsonObject { ["type"] = "block", ["tag"] = "block" } 
            };
            baseConfig["route"] = new JsonObject
            {
                ["rules"] = new JsonArray
                {
                    new JsonObject { ["action"] = "sniff" },
                    new JsonObject { ["ip_is_private"] = true, ["outbound"] = "block" }
                },
                ["final"] = "direct",
                ["auto_detect_interface"] = true
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

        await ssh.ExecuteCommandAsync($"echo '{Convert.ToBase64String(Encoding.UTF8.GetBytes(serviceData.Replace("\r", "")))}' | base64 -d | {sudoPrefix}tee /etc/systemd/system/{bin}.service > /dev/null");
        await ssh.ExecuteCommandAsync($"{sudoPrefix}systemctl daemon-reload");
    }

    private static string GetTransport(string protocol) => protocol.ToLowerInvariant() switch
    {
        "vless" or "trojan" or "shadowsocks" or "vmess" => "tcp",
        "hysteria2" or "hy2" or "tuic" => "udp",
        _ => "tcp"
    };
}