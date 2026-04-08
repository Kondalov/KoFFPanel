using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class XrayCoreService : IXrayCoreService
{
    private readonly IAppLogger _logger;

    public XrayCoreService(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<string> GetCoreStatusAsync(ISshService ssh)
    {
        if (!ssh.IsConnected) return "Offline";
        // Проверяем статус systemd службы (обычно xray или x-ui)
        string result = await ssh.ExecuteCommandAsync("systemctl is-active xray");
        return result.Trim() == "active" ? "Running" : "Stopped";
    }

    public async Task<string> GetCoreLogsAsync(ISshService ssh, int lines = 50)
    {
        if (!ssh.IsConnected) return "Нет соединения...";
        // Читаем логи ядра без зависания
        string result = await ssh.ExecuteCommandAsync($"journalctl -u xray -n {lines} --no-pager -q");
        return string.IsNullOrWhiteSpace(result) ? "Логи пусты или служба не установлена." : result;
    }

    public async Task<bool> RestartCoreAsync(ISshService ssh)
    {
        if (!ssh.IsConnected) return false;
        await ssh.ExecuteCommandAsync("systemctl restart xray");
        _logger.Log("XRAY-CORE", "Отправлена команда на рестарт Xray.");
        return true;
    }

    public async Task<List<VpnClient>> GetClientsAsync(ISshService ssh)
    {
        // Этот метод-заглушка нам больше не нужен, так как мы создали полноценный XrayUserManagerService!
        // Но чтобы интерфейс IXrayCoreService не ругался, просто возвращаем пустой список:
        await Task.CompletedTask;
        return new List<VpnClient>();
    }
}