using KoFFPanel.Domain.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IServerMonitorService
{
    // Основные ресурсы сервера
    Task<(int Cpu, int Ram, int Ssd, string Uptime, string LoadAvg, string NetworkSpeed, int XrayProcesses, int TcpConnections, int SynRecv, int ErrorRate)> GetResourcesAsync(ISshService sshService);

    // Новое: Статистика онлайна пользователей из логов Xray
    Task<List<UserOnlineInfo>> GetUserOnlineStatsAsync(ISshService sshService);

    Task<(bool Success, long RoundtripTime)> PingServerAsync(string ip, int timeoutMs = 2000);
    Task<CoreStatusInfo> GetCoreStatusInfoAsync(ISshService sshService);
}

// Вспомогательный класс для передачи данных парсера
public class UserOnlineInfo
{
    public string Email { get; set; } = "";
    public string LastIp { get; set; } = "";
    public int ActiveSessions { get; set; }
    public string Country { get; set; } = ""; // Новое поле
}

// Вспомогательный класс для данных о ядре
public class CoreStatusInfo
{
    public string Version { get; set; } = "Неизвестно";
    public string ConfigStatus { get; set; } = "Неизвестно";
    public string Uptime { get; set; } = "Остановлен";
    public string MemoryUsage { get; set; } = "0.0 MB";
    public string LastError { get; set; } = "Нет ошибок";
}