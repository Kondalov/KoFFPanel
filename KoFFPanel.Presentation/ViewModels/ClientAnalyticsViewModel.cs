using CommunityToolkit.Mvvm.ComponentModel;
using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;

namespace KoFFPanel.Presentation.ViewModels;

public class TrafficLogItem
{
    public DateTime Date { get; set; }
    public string TrafficUsed { get; set; } = "";
}

public partial class ClientAnalyticsViewModel : ObservableObject
{
    private readonly IClientAnalyticsService _analyticsService;
    private string _serverIp = "";
    private string _email = "";

    [ObservableProperty] private string _title = "Аналитика";
    [ObservableProperty] private ObservableCollection<TrafficLogItem> _trafficLogs = new();
    [ObservableProperty] private ObservableCollection<ClientConnectionLog> _connectionLogs = new();

    // Новая коллекция для нарушений
    [ObservableProperty] private ObservableCollection<ClientViolationLog> _violationLogs = new();

    public ClientAnalyticsViewModel(IClientAnalyticsService analyticsService)
    {
        _analyticsService = analyticsService;
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
        var traffic = await _analyticsService.GetTrafficLogsAsync(_serverIp, _email, 30);
        var connections = await _analyticsService.GetConnectionLogsAsync(_serverIp, _email);
        var violations = await _analyticsService.GetViolationLogsAsync(_serverIp, _email);

        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            TrafficLogs.Clear();
            foreach (var t in traffic) TrafficLogs.Add(new TrafficLogItem { Date = t.Date, TrafficUsed = FormatBytes(t.BytesUsed) });

            ConnectionLogs.Clear();
            foreach (var c in connections) ConnectionLogs.Add(c);

            ViolationLogs.Clear();
            foreach (var v in violations) ViolationLogs.Add(v);
        });
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