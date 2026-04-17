using KoFFPanel.Application.Interfaces;
using System;
using System.IO;

namespace KoFFPanel.Infrastructure.Services;

public class AppLogger : IAppLogger
{
    private readonly string _logDir;
    private readonly string _logFile;
    private readonly string _terminalLogFile;
    private readonly string _botLogFile; // Добавили переменную для файла бота

    public AppLogger()
    {
        _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        if (!Directory.Exists(_logDir))
        {
            Directory.CreateDirectory(_logDir);
        }

        _logFile = Path.Combine(_logDir, "app_analytics.log");
        _terminalLogFile = Path.Combine(_logDir, "terminal.log");

        // ИСПРАВЛЕНИЕ: Создаем выделенный лог для Telegram Бота
        _botLogFile = Path.Combine(_logDir, "BOT.log");

        Log("SESSION", "\n====================== СТАРТ СЕССИИ ======================");
    }

    public void Log(string module, string message)
    {
        try
        {
            string logEntry = $"[{DateTime.Now:HH:mm:ss.fff}] [{module}] {message}\n";

            // Умная маршрутизация логов
            if (module.StartsWith("TERM") || module.StartsWith("SSH"))
            {
                File.AppendAllText(_terminalLogFile, logEntry);
            }
            else if (module.StartsWith("BOT")) // Все логи интеграции летят сюда
            {
                File.AppendAllText(_botLogFile, logEntry);
            }
            else
            {
                File.AppendAllText(_logFile, logEntry);
            }
        }
        catch { /* Игнорируем ошибки ввода-вывода, чтобы не крашнуть панель */ }
    }
}