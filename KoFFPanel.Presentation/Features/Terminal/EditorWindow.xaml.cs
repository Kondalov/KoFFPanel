using System;
using System.Windows;
using KoFFPanel.Presentation.Features.Shared.Dialogs;

namespace KoFFPanel.Presentation.Features.Terminal;

public partial class EditorWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly EditorViewModel _viewModel;

    public EditorWindow(EditorViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.InitializeWebView(EditorWebView);
        _viewModel.OnSaveAsRequested += ViewModel_OnSaveAsRequested;
        this.Closing += EditorWindow_Closing;
    }

    private void ViewModel_OnSaveAsRequested(string currentName)
    {
        var dialog = new InputTextDialog(this, "Сохранить как", "Введите новое имя файла:", currentName);
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
