using KoFFPanel.Domain.Entities;
using KoFFPanel.Presentation.Features.Terminal;
using KoFFPanel.Presentation.Features.Shared.Dialogs;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Linq;

namespace KoFFPanel.Presentation.Features.Terminal;

public partial class TerminalWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly TerminalViewModel _viewModel;
    private readonly Wpf.Ui.SnackbarService _snackbarService;

    public TerminalWindow(TerminalViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _snackbarService = new Wpf.Ui.SnackbarService();
        _snackbarService.SetSnackbarPresenter(TerminalSnackbarPresenter);

        _viewModel.InitializeWebView(TerminalWebView);
        _viewModel.OnFileReadyForEdit += ViewModel_OnFileReadyForEdit;
        this.Loaded += TerminalWindow_Loaded;
    }

    private void ViewModel_OnFileReadyForEdit(string localPath, string remotePath)
    {
        var editorVm = new EditorViewModel(_viewModel.Logger, _viewModel.SshService);
        editorVm.Initialize(localPath, remotePath);

        editorVm.OnSaveRequested = async (local, remote) =>
        {
            try
            {
                await _viewModel.UploadEditedFileAsync(local, remote);
                return true;
            }
            catch
            {
                return false;
            }
        };

        var editorWindow = new EditorWindow(editorVm) { Owner = this };
        editorWindow.Show();
    }

    private async void TerminalWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.ConnectAsync();
    }

    private void ExplorerItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListViewItem item && item.DataContext is RemoteFileItem remoteFile)
        {
            _viewModel.NavigateCommand.Execute(remoteFile);
        }
    }

    private void NavigateUp_Click(object sender, MouseButtonEventArgs e)
    {
        _viewModel.NavigateCommand.Execute(new RemoteFileItem { Name = "..", IsDirectory = true });
    }

    private async void CopyTerminal_Click(object sender, RoutedEventArgs e)
    {
        await TerminalWebView.ExecuteScriptAsync("if(window.copyAllTerminal) window.copyAllTerminal();");
        CopyPopup.IsOpen = true;
        await System.Threading.Tasks.Task.Delay(1500);
        CopyPopup.IsOpen = false;
    }

    private async void CurrentDirectory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !string.IsNullOrEmpty(_viewModel.CurrentDirectory))
        {
            Clipboard.SetText(_viewModel.CurrentDirectory);
            CopyPopup.IsOpen = true;
            await System.Threading.Tasks.Task.Delay(1500);
            CopyPopup.IsOpen = false;
        }
    }
    private void CommandTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            e.Handled = true;
            _viewModel.SendManualCommand();
        }
    }

    private void SendCommand_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SendManualCommand();
    }

    private void MenuItem_Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is RemoteFileItem remoteFile)
        {
            _viewModel.NavigateCommand.Execute(remoteFile);
        }
    }

    private void MenuItem_Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is RemoteFileItem remoteFile)
        {
            if (remoteFile.Name == "..") return;
            var dialog = new InputTextDialog(this, "Переименование", "Введите новое имя:", remoteFile.Name);
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                _viewModel.RenameItemCommand.Execute((remoteFile.Name, dialog.InputText.Trim()));
            }
        }
    }

    private void MenuItem_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is RemoteFileItem remoteFile)
        {
            if (remoteFile.Name == "..") return;
            var result = MessageBox.Show(this, $"Вы уверены, что хотите удалить '{remoteFile.Name}'?", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes) _viewModel.DeleteItemCommand.Execute(remoteFile);
        }
    }

    private void MenuItem_CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputTextDialog(this, "Создать папку", "Введите имя новой папки:");
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            _viewModel.CreateItemCommand.Execute((dialog.InputText.Trim(), true));
        }
    }

    private void MenuItem_CreateFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputTextDialog(this, "Создать файл", "Введите имя нового файла:");
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            _viewModel.CreateItemCommand.Execute((dialog.InputText.Trim(), false));
        }
    }

    private void AddSnippetCategory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputTextDialog(this, "Новая категория", "Введите название (например: Docker):");
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText)) _viewModel.AddSnippetCategory(dialog.InputText.Trim());
    }

    private void AddSnippetSubCategory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new InputTextDialog(this, "Новая подкатегория", "Введите название (например: Логи):");
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText)) _viewModel.AddSnippetSubCategory(dialog.InputText.Trim());
    }

    private void AddSnippet_Click(object sender, RoutedEventArgs e) { SaveNewSnippet(); }
    private void NewSnippetCmd_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) SaveNewSnippet(); }
    private void SaveNewSnippet()
    {
        if (!string.IsNullOrWhiteSpace(NewSnippetCmd.Text))
        {
            _viewModel.AddSnippet(NewSnippetDesc.Text, NewSnippetCmd.Text);
            NewSnippetDesc.Text = ""; NewSnippetCmd.Text = "";
        }
    }

    private void TabsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer scroller)
        {
            if (e.Delta > 0) scroller.LineLeft(); else scroller.LineRight();
            e.Handled = true;
        }
    }

    private void Category_Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is SnippetCategory cat)
        {
            var dialog = new InputTextDialog(this, "Переименование", "Новое название категории:", cat.Name);
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText)) { cat.Name = dialog.InputText.Trim(); _viewModel.SaveSnippets(); }
        }
    }

    private void Category_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is SnippetCategory cat)
        {
            if (MessageBox.Show(this, $"Удалить категорию '{cat.Name}'?", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes) _viewModel.DeleteCategoryCommand.Execute(cat);
        }
    }

    private void SubCategory_Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is SnippetSubCategory sub)
        {
            var dialog = new InputTextDialog(this, "Переименование", "Новое название подкатегории:", sub.Name);
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText)) { sub.Name = dialog.InputText.Trim(); _viewModel.SaveSnippets(); }
        }
    }

    private void SubCategory_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is SnippetSubCategory sub)
        {
            if (MessageBox.Show(this, $"Удалить подкатегорию '{sub.Name}'?", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                if (_viewModel.SelectedSnippetCategory != null) { _viewModel.SelectedSnippetCategory.SubCategories.Remove(sub); _viewModel.SaveSnippets(); }
            }
        }
    }

    private void ClearTerminal_Click(object sender, MouseButtonEventArgs e)
    {
        string currentText = _viewModel.CommandInput;
        _viewModel.CommandInput = "clear"; _viewModel.SendManualCommand();
        _viewModel.CommandInput = currentText;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        TerminalWebView.Dispose();
        base.OnClosed(e);
    }
}
