using KoFFPanel.Presentation.ViewModels;
using System;
using System.Windows;

namespace KoFFPanel.Presentation.Views;

public partial class EditorWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly EditorViewModel _viewModel;

    public EditorWindow(EditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.InitializeWebView(EditorWebView);

        // ИСПРАВЛЕНИЕ: Подписка на запрос "Сохранить как" от ViewModel
        _viewModel.OnSaveAsRequested += ViewModel_OnSaveAsRequested;

        this.Closing += EditorWindow_Closing;
    }

    private void ViewModel_OnSaveAsRequested(string currentName)
    {
        // Вызываем наше кастомное окно ввода
        var dialog = new Extras.InputTextDialog(this, "Сохранить как", "Введите новое имя файла:", currentName);
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            _viewModel.PerformSaveAs(dialog.InputText.Trim());
        }
    }

    private void EditorWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_viewModel.HasUnsavedChanges)
        {
            var result = MessageBox.Show(this, "Есть несохраненные изменения. Закрыть без сохранения?", "Внимание", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        EditorWebView.Dispose();
        base.OnClosed(e);
    }
}