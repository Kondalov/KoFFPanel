using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System; // <-- Добавлено
using System.Threading.Tasks;
using System.Windows;

namespace KoFFPanel.Presentation.ViewModels;

public partial class ClientConfigViewModel : ObservableObject
{
    [ObservableProperty] private string _email = string.Empty;
    [ObservableProperty] private string _vlessLink = string.Empty;
    [ObservableProperty] private string _clientJson = string.Empty;
    [ObservableProperty] private string _httpLink = string.Empty;

    [ObservableProperty] private bool _isVlessCopied;
    [ObservableProperty] private bool _isJsonCopied;
    [ObservableProperty] private bool _isHttpCopied;

    // --- ДОБАВЛЕНО ДЛЯ ЗАКРЫТИЯ ---
    public Action? CloseAction { get; set; }

    [RelayCommand]
    private void CloseWindow()
    {
        CloseAction?.Invoke();
    }
    // ------------------------------

    public void Initialize(string email, string vlessLink, string clientJson, string httpLink)
    {
        Email = email;
        VlessLink = vlessLink;
        ClientJson = clientJson;
        HttpLink = httpLink;
    }

    [RelayCommand]
    private async Task CopyVlessAsync()
    {
        Clipboard.SetText(VlessLink);
        IsVlessCopied = true;
        await Task.Delay(2000);
        IsVlessCopied = false;
    }

    [RelayCommand]
    private async Task CopyJsonAsync()
    {
        Clipboard.SetText(ClientJson);
        IsJsonCopied = true;
        await Task.Delay(2000);
        IsJsonCopied = false;
    }

    [RelayCommand]
    private async Task CopyHttpAsync()
    {
        Clipboard.SetText(HttpLink);
        IsHttpCopied = true;
        await Task.Delay(2000);
        IsHttpCopied = false;
    }
}