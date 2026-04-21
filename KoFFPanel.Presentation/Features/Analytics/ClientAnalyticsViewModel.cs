using CommunityToolkit.Mvvm.ComponentModel;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.Features.Analytics;

// Умные обертки для интерфейса (DTO -> View)
public class TrafficItemUI
{
    public string DateStr { get; set; } = "";
    public string TrafficUsedStr { get; set; } = "";
    public double BarWidth { get; set; } = 0;
    public string StatusText { get; set; } = "";
    public string StatusColor { get; set; } = "";
    public string GradientStart { get; set; } = "";
    public string GradientEnd { get; set; } = "";
}

public class ConnectionItemUI
{
    public string IpAddress { get; set; } = "";
    public string Country { get; set; } = "";
    public string FirstSeenStr { get; set; } = "";
    public string LastSeenStr { get; set; } = "";
}

public class ViolationItemUI
{
    public string DateStr { get; set; } = "";
    public string ViolationText { get; set; } = "";
}

public partial class ClientAnalyticsViewModel : ObservableObject
{
    private readonly IClientAnalyticsService _analyticsService;
    private readonly IAppLogger _logger;
    private string _serverIp = "";
    private string _email = "";

    [ObservableProperty] private string _title = "Аналитика";
    [ObservableProperty] private int _ipCount = 0;
    [ObservableProperty] private int _violationsCount = 0;

    [ObservableProperty] private ObservableCollection<TrafficItemUI> _trafficLogs = new();
    [ObservableProperty] private ObservableCollection<ConnectionItemUI> _connectionLogs = new();
    [ObservableProperty] private ObservableCollection<ViolationItemUI> _violationLogs = new();

    // Обязательно добавляем IAppLogger в конструктор для мощной телеметрии!
    public ClientAnalyticsViewModel(IClientAnalyticsService analyticsService, IAppLogger logger)
    {
        _analyticsService = analyticsService;
        _logger = logger;
    }

    public void Initialize(string serverIp, string email)
    {
        _serverIp = serverIp;
        _email = email;
        Title = $"Аналитика пользователя: {email}";
        _ = LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        try
        {
            _logger.Log("UI-ANALYTICS", $"Запрос данных для отрисовки окна аналитики {_email}");

            var traffic = await _analyticsService.GetTrafficLogsAsync(_serverIp, _email, 30);
            var connections = await _analyticsService.GetConnectionLogsAsync(_serverIp, _email);
            var violations = await _analyticsService.GetViolationLogsAsync(_serverIp, _email);

            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                // 1. Обработка Журнала IP адресов
                ConnectionLogs.Clear();
                foreach (var c in connections)
                {
                    ConnectionLogs.Add(new ConnectionItemUI
                    {
                        IpAddress = c.IpAddress ?? "Unknown",
                        Country = c.Country ?? "??",
                        FirstSeenStr = c.FirstSeen.ToString("dd MMM HH:mm"),
                        LastSeenStr = c.LastSeen.ToString("dd MMM HH:mm")
                    });
                }
                IpCount = connections.Count;

                // 2. Умный расчет градиентов и прогресс-баров для трафика
                TrafficLogs.Clear();
                long maxTraffic = traffic.Any() ? traffic.Max(x => x.BytesUsed) : 0;
                if (maxTraffic == 0) maxTraffic = 1; // Защита от деления на ноль

                foreach (var t in traffic)
                {
                    double percent = (double)t.BytesUsed / maxTraffic;
                    double barWidth = percent * 150; // Максимальная ширина бара 150px

                    string status = "Низкий расход";
                    string statusColor = "Transparent";
                    string gradStart = "#00f2ff";
                    string gradEnd = "#00ff88";

                    if (t.BytesUsed > 5L * 1024 * 1024 * 1024) // Больше 5 ГБ
                    {
                        status = "Высокий расход"; statusColor = "#33ff4444"; gradStart = "#ffaa00"; gradEnd = "#ff4444";
                    }
                    else if (t.BytesUsed > 1L * 1024 * 1024 * 1024) // Больше 1 ГБ
                    {
                        status = "Средний расход"; statusColor = "Transparent"; gradStart = "#00ff88"; gradEnd = "#ffaa00";
                    }

                    TrafficLogs.Add(new TrafficItemUI
                    {
                        DateStr = t.Date.ToString("dd MMMM yyyy"),
                        TrafficUsedStr = FormatBytes(t.BytesUsed),
                        BarWidth = barWidth,
                        StatusText = status,
                        StatusColor = statusColor,
                        GradientStart = gradStart,
                        GradientEnd = gradEnd
                    });
                }

                // 3. Обработка Нарушений
                ViolationLogs.Clear();
                foreach (var v in violations)
                {
                    ViolationLogs.Add(new ViolationItemUI
                    {
                        DateStr = v.Date.ToString("dd MMM yyyy, HH:mm"),
                        ViolationText = v.ViolationType ?? "Неизвестное нарушение"
                    });
                }
                ViolationsCount = violations.Count;

                _logger.Log("UI-ANALYTICS", "Данные успешно отрендерены в UI.");
            });
        }
        catch (Exception ex)
        {
            _logger.Log("UI-ERR", $"Ошибка рендера аналитики: {ex.Message}");
        }
    }

    private string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0; decimal number = bytes;
        while (Math.Round(number / 1024) >= 1) { number /= 1024; counter++; }
        return string.Format("{0:n2} {1}", number, suffixes[counter]);
    }
}