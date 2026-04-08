using KoFFPanel.Application.Interfaces;
using System;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class CoreDeploymentService : ICoreDeploymentService
{
    private readonly IAppLogger _logger;

    public CoreDeploymentService(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<(bool IsSuccess, string Message)> RunPreFlightChecksAsync(ISshService ssh)
    {
        if (!ssh.IsConnected) return (false, "Нет SSH подключения");

        // Умная проверка сервера (защита от дурака)
        string checkScript = @"
            if [ ""$EUID"" -ne 0 ]; then echo 'ERROR|Нужны права root (sudo) для установки ядер.'; exit 0; fi
            if ! command -v systemctl >/dev/null 2>&1; then echo 'ERROR|Сервер не поддерживает systemd. Установка невозможна.'; exit 0; fi
            if ! command -v curl >/dev/null 2>&1; then echo 'ERROR|Пакет curl не установлен. Выполните apt install curl.'; exit 0; fi
            if ! curl -s --head --request GET https://github.com | grep ""200"" > /dev/null; then echo 'ERROR|Нет доступа к GitHub (возможно, IP заблокирован).'; exit 0; fi
            echo 'READY|Сервер готов к установке.'
        ";

        string result = await ssh.ExecuteCommandAsync(checkScript);
        result = result.Trim();

        if (result.StartsWith("ERROR|"))
        {
            _logger.Log("DEPLOY-CHECK", $"Провал: {result}");
            return (false, result.Split('|')[1]);
        }

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
        _logger.Log("DEPLOY", $"Старт УМНОЙ установки Xray ({targetVersion})");

        // УМНЫЙ РЕЖИМ: Тотальная зачистка конфликтов
        string smartCleanup = @"
            echo '[SMART MODE] Начинаем зачистку системы...'
            systemctl stop xray sing-box v2ray 2>/dev/null || true
            systemctl disable xray sing-box v2ray 2>/dev/null || true
            rm -rf /usr/local/bin/xray /usr/local/bin/sing-box /usr/local/etc/xray /usr/local/etc/sing-box
            rm -rf /etc/systemd/system/xray* /etc/systemd/system/sing-box*
            systemctl daemon-reload
            echo '[SMART MODE] Освобождаем порты 80 и 443...'
            fuser -k 443/tcp 2>/dev/null || true
            fuser -k 80/tcp 2>/dev/null || true
            echo '[SMART MODE] Система стерильна. Запускаем установку!'
        ";
        await ssh.ExecuteCommandAsync(smartCleanup);

        string installCmd = targetVersion == "latest"
            ? "bash -c \"$(curl -L https://github.com/XTLS/Xray-install/raw/main/install-release.sh)\" @ install"
            : $"bash -c \"$(curl -L https://github.com/XTLS/Xray-install/raw/main/install-release.sh)\" @ install --version {targetVersion}";

        string log = await ssh.ExecuteCommandAsync(installCmd);
        return (log.Contains("installed") || log.Contains("success"), log);
    }

    public async Task<(bool IsSuccess, string Log)> InstallSingBoxAsync(ISshService ssh, string targetVersion = "latest")
    {
        _logger.Log("DEPLOY", $"Старт УМНОЙ установки Sing-Box ({targetVersion})");

        // Тот же скрипт тотальной зачистки
        string smartCleanup = @"
            systemctl stop xray sing-box v2ray 2>/dev/null || true
            systemctl disable xray sing-box v2ray 2>/dev/null || true
            rm -rf /usr/local/bin/xray /usr/local/bin/sing-box /usr/local/etc/xray /usr/local/etc/sing-box
            rm -rf /etc/systemd/system/xray* /etc/systemd/system/sing-box*
            systemctl daemon-reload
            fuser -k 443/tcp 2>/dev/null || true
            fuser -k 80/tcp 2>/dev/null || true
        ";
        await ssh.ExecuteCommandAsync(smartCleanup);

        string installCmd = "bash <(curl -fsSL https://sing-box.app/install.sh)";
        string log = await ssh.ExecuteCommandAsync(installCmd);
        return (log.Contains("Installed") || log.Contains("success"), log);
    }
}