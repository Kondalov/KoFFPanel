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

        // Передаем WebView2 во ViewModel для настройки Monaco Editor
        _viewModel.InitializeWebView(EditorWebView);

        // Обработка закрытия (проверка несохраненных изменений)
        this.Closing += EditorWindow_Closing;
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