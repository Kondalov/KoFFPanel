using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;

namespace KoFFPanel.Presentation.ViewModels;

public partial class AddClientViewModel : ObservableObject
{
    [ObservableProperty] private string _clientName = "";
    [ObservableProperty] private int _trafficLimitGb = 0;
    [ObservableProperty] private DateTime? _expiryDate = null;
    [ObservableProperty] private string _note = ""; // Новое свойство

    // Динамический заголовок окна и текст кнопки
    [ObservableProperty] private string _windowTitle = "Добавить пользователя";
    [ObservableProperty] private string _actionButtonText = "Создать";

    // Флаг: мы создаем или редактируем?
    public bool IsEditMode { get; private set; } = false;

    public bool IsSuccess { get; private set; } = false;
    public Action? CloseAction { get; set; }

    // Метод для загрузки существующих данных при Редактировании
    public void LoadForEdit(string currentName, long currentLimitBytes, DateTime? currentExpiry, string currentNote = "")
    {
        IsEditMode = true;
        WindowTitle = "Редактировать пользователя";
        ActionButtonText = "Сохранить";

        ClientName = currentName;
        TrafficLimitGb = (int)(currentLimitBytes / 1024 / 1024 / 1024);
        ExpiryDate = currentExpiry;
        Note = currentNote ?? "";
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(ClientName)) return;

        IsSuccess = true;
        CloseAction?.Invoke();
    }

    [RelayCommand]
    private void Cancel()
    {
        IsSuccess = false;
        CloseAction?.Invoke();
    }
}