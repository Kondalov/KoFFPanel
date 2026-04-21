using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Org.BouncyCastle.Utilities;
using System;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace KoFFPanel.Presentation.Features.Config;

public partial class CustomConfigViewModel : ObservableObject
{
    public Action? CloseAction { get; set; }

    [ObservableProperty] private string _selectedKernel = "Xray-core";
    [ObservableProperty] private string _selectedIpProtocol = "IPv4";

    [ObservableProperty] private string _sniInput = "";
    [ObservableProperty] private string _selectedSni = "";

    [ObservableProperty] private string _port = "443";
    [ObservableProperty] private string _uuid = Guid.NewGuid().ToString();
    [ObservableProperty] private string _shortId = "1965d789";

    [ObservableProperty] private string _statusMessage = "Заполните данные для конфигурации";
    [ObservableProperty] private bool _isError = false;

    // Списки для ComboBox
    public ObservableCollection<string> Kernels { get; } = new() { "Xray-core", "Sing-box" };
    public ObservableCollection<string> IpProtocols { get; } = new() { "IPv4", "IPv6", "Dual Stack" };
    public ObservableCollection<string> TopSniList { get; } = new()
    {
        "www.google.com", "www.microsoft.com", "www.apple.com",
        "www.cloudflare.com", "www.github.com", "www.amazon.com",
        "www.wikipedia.org", "www.youtube.com", "www.twitter.com", "www.linkedin.com"
    };

    // Автоматически переносим выбранный SNI в текстовое поле
    partial void OnSelectedSniChanged(string value)
    {
        if (!string.IsNullOrEmpty(value)) SniInput = value;
    }

    [RelayCommand]
    private void GenerateUuid()
    {
        Uuid = Guid.NewGuid().ToString();
        StatusMessage = "✅ Новый UUID успешно сгенерирован!";
        IsError = false;
    }

    [RelayCommand]
    private void ApplyConfig()
    {
        if (!ValidateFoolproof()) return;

        // Здесь будет логика применения твоей конфигурации
        StatusMessage = "🚀 КОНФИГУРАЦИЯ ИДЕАЛЬНА! Подготовка к отправке на сервер...";
        IsError = false;
    }

    [RelayCommand]
    private void Cancel() => CloseAction?.Invoke();

    // УМНАЯ ЗАЩИТА ОТ ДУРАКА (Foolproof)
    private bool ValidateFoolproof()
    {
        // 1. Проверка порта (Только числа от 1 до 65535)
        if (!int.TryParse(Port, out int portNum) || portNum < 1 || portNum > 65535)
        {
            ShowError("ОШИБКА: Порт должен быть числом от 1 до 65535!");
            return false;
        }

        // 2. Проверка UUID (Строгий формат 8-4-4-4-12)
        if (!Guid.TryParse(Uuid, out _))
        {
            ShowError("ОШИБКА: Неверный формат UUID! Нажмите кнопку генерации.");
            return false;
        }

        // 3. Проверка Short ID (Только HEX символы, максимум 16 знаков)
        if (string.IsNullOrWhiteSpace(ShortId) || !Regex.IsMatch(ShortId, "^[0-9a-fA-F]+$") || ShortId.Length > 16)
        {
            ShowError("ОШИБКА: Short ID должен состоять только из HEX-символов (0-9, a-f)!");
            return false;
        }

        // 4. Проверка SNI
        if (string.IsNullOrWhiteSpace(SniInput))
        {
            ShowError("ОШИБКА: Укажите SNI для маскировки!");
            return false;
        }

        return true;
    }

    private void ShowError(string msg)
    {
        StatusMessage = msg;
        IsError = true;
    }
}