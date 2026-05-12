using CommunityToolkit.Mvvm.ComponentModel;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Runtime.Versioning;
using System;

namespace KoFFPanel.Presentation.Features.Analytics;

[SupportedOSPlatform("windows")]
public class BehaviorItemUI
{
    public string DateStr { get; set; } = "";
    public int MaxSessions { get; set; }
    public int AsnCount { get; set; }
    public int GeoJumps { get; set; }
    public string TrafficSpike { get; set; } = "";
    public int RiskScore { get; set; }
    public string RiskColor { get; set; } = "";
    public string RiskText { get; set; } = "";
}

[SupportedOSPlatform("windows")]
public partial class FraudScoringViewModel : ObservableObject
{
    private readonly IAntiFraudService _antiFraudService;
    private string _serverIp = "";
    private string _email = "";

    [ObservableProperty] private string _title = "Фрод-скоринг (Месяц)";
    [ObservableProperty] private ObservableCollection<BehaviorItemUI> _behaviorLogs = new();

    public FraudScoringViewModel(IAntiFraudService antiFraudService)
    {
        _antiFraudService = antiFraudService;
    }

    public void Initialize(string serverIp, string email)
    {
        _serverIp = serverIp;
        _email = email;
        Title = $"Фрод-анализ: {email} (За 30 дней)";
        _ = LoadDataAsync();
    }

    private async Task LoadDataAsync()
    {
        var logs = await _antiFraudService.GetMonthlyBehaviorAsync(_serverIp, _email);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            BehaviorLogs.Clear();
            foreach (var log in logs)
            {
                string color = "#00ff88"; // Green
                string text = "Норма";

                if (log.RiskScore >= 100) { color = "#ff4444"; text = "Критичный риск (БАН)"; }
                else if (log.RiskScore >= 50) { color = "#ffaa00"; text = "Высокий риск"; }
                else if (log.RiskScore > 0) { color = "#00f2ff"; text = "Подозрение"; }

                BehaviorLogs.Add(new BehaviorItemUI
                {
                    DateStr = log.Date.ToString("dd MMM yyyy"),
                    MaxSessions = log.MaxConcurrentSessions,
                    AsnCount = log.UniqueAsnCount,
                    GeoJumps = log.GeoJumpsCount,
                    TrafficSpike = FormatBytes(log.BytesUsedSpike),
                    RiskScore = log.RiskScore,
                    RiskColor = color,
                    RiskText = text
                });
            }
        });
    }

    private string FormatBytes(long bytes)
    {
        if (bytes == 0) return "-";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0; decimal number = bytes;
        while (Math.Round(number / 1024) >= 1) { number /= 1024; counter++; }
        return string.Format("{0:n2} {1}", number, suffixes[counter]);
    }
}