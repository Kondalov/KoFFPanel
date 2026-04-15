using KoFFPanel.Application.Interfaces;
using System;
using System.IO;

namespace KoFFPanel.Infrastructure.Services;

public class AppLogger : IAppLogger
{
    private readonly string _logDir;
    private readonly string _logFile;
    private readonly string _terminalLogFile;

    public AppLogger()
    {
        _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        if (!Directory.Exists(_logDir))
        {
            Directory.CreateDirectory(_logDir);
        }

        _logFile = Path.Combine(_logDir, "app_analytics.log");

        // ИСПРАВЛЕНИЕ: Создаем выделенный лог для терминала!
        _terminalLogFile = Path.Combine(_logDir, "terminal.log");

        Log("SESSION", "\n====================== СТАРТ СЕССИИ ======================");
    }

    public void Log(string module, string message)
    {
        try
        {
            string logEntry = $"[{DateTime.Now:HH:mm:ss.fff}] [{module}] {message}\n";

            // ИСПРАВЛЕНИЕ: Умная маршрутизация логов
            if (module.StartsWith("TERM") || module.StartsWith("SSH"))
            {
                File.AppendAllText(_terminalLogFile, logEntry);
            }
            else
            {
                File.AppendAllText(_logFile, logEntry);
            }
        }
        catch { /* Игнорируем ошибки ввода-вывода, чтобы не крашнуть панель */ }
    }
}