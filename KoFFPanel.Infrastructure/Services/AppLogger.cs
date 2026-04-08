using KoFFPanel.Application.Interfaces;
using System;
using System.IO;

namespace KoFFPanel.Infrastructure.Services;

public class AppLogger : IAppLogger
{
    private readonly string _logDir;
    private readonly string _logFile;

    public AppLogger()
    {
        // Умная логика: создаем папку Logs рядом с exe-файлом
        _logDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Logs");
        if (!Directory.Exists(_logDir))
        {
            Directory.CreateDirectory(_logDir);
        }

        _logFile = Path.Combine(_logDir, "app_analytics.log");
    }

    public void Log(string module, string message)
    {
        try
        {
            string logEntry = $"[{DateTime.Now:HH:mm:ss.fff}] [{module}] {message}\n";
            File.AppendAllText(_logFile, logEntry);
        }
        catch { /* Игнорируем ошибки логирования, чтобы не уронить приложение */ }
    }
}