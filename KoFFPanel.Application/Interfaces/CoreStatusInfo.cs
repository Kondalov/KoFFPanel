namespace KoFFPanel.Application.Interfaces;

public class CoreStatusInfo
{
    public string Version { get; set; } = "Неизвестно";
    public string ConfigStatus { get; set; } = "Неизвестно";
    public string Uptime { get; set; } = "Остановлен";
    public string MemoryUsage { get; set; } = "0.0 MB";
    public string LastError { get; set; } = "Нет ошибок";
}
