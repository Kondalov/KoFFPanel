using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace KoFFPanel.Domain.Entities;

// Используем нативный INotifyPropertyChanged, чтобы не тащить внешние UI-библиотеки в слой Domain
public class VpnClient : INotifyPropertyChanged
{
    private string _email = "";
    public int Id { get; set; }
    public string ServerIp { get; set; } = "";

    private bool _isAntiFraudEnabled = true;
    public bool IsAntiFraudEnabled
    {
        get => _isAntiFraudEnabled;
        set { _isAntiFraudEnabled = value; OnPropertyChanged(); }
    }

    private string _uuid = "";
    public string Uuid
    {
        get => _uuid;
        set { _uuid = value; OnPropertyChanged(); }
    }

    private string _country = "🌍 ??";
    public string Country
    {
        get => _country;
        set { _country = value; OnPropertyChanged(); }
    }

    private DateTime? _expiryDate;
    public DateTime? ExpiryDate
    {
        get => _expiryDate;
        set { _expiryDate = value; OnPropertyChanged(); OnPropertyChanged(nameof(ExpiryString)); }
    }

    public string Email
    {
        get => _email;
        set { _email = value; OnPropertyChanged(); }
    }

    private string _protocol = "VLESS";
    public string Protocol
    {
        get => _protocol;
        set { _protocol = value; OnPropertyChanged(); }
    }

    private bool _isActive = true;
    public bool IsActive
    {
        get => _isActive;
        set
        {
            _isActive = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusString)); // Обновляем зависимое свойство
        }
    }

    private string _vlessLink = "";
    public string VlessLink
    {
        get => _vlessLink;
        set { _vlessLink = value; OnPropertyChanged(); }
    }

    private long _trafficUsed = 0;
    public long TrafficUsed
    {
        get => _trafficUsed;
        set
        {
            _trafficUsed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrafficUsageString)); // Магия: обновляем строку в UI при изменении байтов
        }
    }

    private long _trafficLimit = 0;
    public long TrafficLimit
    {
        get => _trafficLimit;
        set
        {
            _trafficLimit = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrafficUsageString));
        }
    }

    // --- НОВЫЕ ПОЛЯ ДЛЯ КОНТРОЛЯ ПОЛЬЗОВАТЕЛЕЙ ---

    private string _note = "";
    public string Note
    {
        get => _note;
        set { _note = value; OnPropertyChanged(); }
    }

    private string _lastIp = "";
    public string LastIp
    {
        get => _lastIp;
        set { _lastIp = value; OnPropertyChanged(); }
    }

    private int _activeConnections = 0;
    public int ActiveConnections
    {
        get => _activeConnections;
        set { _activeConnections = value; OnPropertyChanged(); }
    }

    private DateTime? _lastOnline;
    public DateTime? LastOnline
    {
        get => _lastOnline;
        set
        {
            _lastOnline = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LastOnlineString));
        }
    }

    // --- ВЫЧИСЛЯЕМЫЕ СВОЙСТВА ДЛЯ UI ---

    public string TrafficUsageString
    {
        get
        {
            string used = FormatBytes(TrafficUsed);
            string limit = TrafficLimit <= 0 ? "∞" : FormatBytes(TrafficLimit);
            return $"{used} / {limit}";
        }
    }

    public string ExpiryString => ExpiryDate.HasValue
        ? ExpiryDate.Value.ToString("dd MMM yyyy")
        : "Бессрочно";

    public string StatusString => IsActive ? "Активен" : "Отключен";

    public string LastOnlineString => LastOnline.HasValue
        ? LastOnline.Value.ToString("dd MMM HH:mm")
        : "Никогда";

    // Умный конвертер байтов в KB, MB, GB, TB
    private string FormatBytes(long bytes)
    {
        if (bytes == 0) return "0 B";
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        int counter = 0;
        decimal number = bytes;

        while (Math.Round(number / 1024) >= 1)
        {
            number /= 1024;
            counter++;
        }

        return string.Format("{0:n2} {1}", number, suffixes[counter]);
    }

    // Стандартная реализация уведомлений интерфейса об изменениях
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}