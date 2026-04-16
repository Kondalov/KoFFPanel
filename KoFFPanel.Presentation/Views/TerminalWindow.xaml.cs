using KoFFPanel.Domain.Entities;
using KoFFPanel.Presentation.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace KoFFPanel.Presentation.Views;

public partial class TerminalWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly TerminalViewModel _viewModel;
    private readonly Wpf.Ui.SnackbarService _snackbarService;

    public TerminalWindow(TerminalViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        // Инициализация сервиса всплывающих подсказок (Снекбар)
        _snackbarService = new Wpf.Ui.SnackbarService();
        _snackbarService.SetSnackbarPresenter(TerminalSnackbarPresenter);

        _viewModel.InitializeWebView(TerminalWebView);
        _viewModel.OnFileReadyForEdit += ViewModel_OnFileReadyForEdit;
        this.Loaded += TerminalWindow_Loaded;
    }

    private void ViewModel_OnFileReadyForEdit(string localPath, string remotePath)
    {
        // Создаем ViewModel для редактора и передаем ей пути
        var editorVm = new EditorViewModel(_viewModel.Logger);
        editorVm.Initialize(localPath, remotePath);

        // Когда в редакторе нажимают "Сохранить", мы просим терминал загрузить файл на сервер
        editorVm.OnSaveRequested += async (local, remote) =>
        {
            await _viewModel.UploadEditedFileAsync(local, remote);
        };

        // Открываем окно редактора поверх терминала
        var editorWindow = new EditorWindow(editorVm)
        {
            Owner = this
        };

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
    private async void CurrentDirectory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && !string.IsNullOrEmpty(_viewModel.CurrentDirectory))
        {
            Clipboard.SetText(_viewModel.CurrentDirectory);

            // Открываем всплывающую подсказку у курсора мыши
            CopyPopup.IsOpen = true;

            // Ждем 1.5 секунды и скрываем её
            await System.Threading.Tasks.Task.Delay(1500);
            CopyPopup.IsOpen = false;
        }
    }
    private void CommandTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Если нажат Enter БЕЗ Shift
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

    // --- ОБРАБОТЧИКИ КОНТЕКСТНОГО МЕНЮ SFTP ---

    private void MenuItem_Open_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is RemoteFileItem remoteFile)
        {
            // Открываем как при двойном клике (пока редактор не готов)
            _viewModel.NavigateCommand.Execute(remoteFile);
        }
    }

    private void MenuItem_Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is RemoteFileItem remoteFile)
        {
            if (remoteFile.Name == "..") return; // Нельзя переименовать кнопку "Назад"

            var dialog = new Extras.InputTextDialog(this, "Переименовать", "Введите новое имя:", remoteFile.Name);
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
            if (result == MessageBoxResult.Yes)
            {
                _viewModel.DeleteItemCommand.Execute(remoteFile);
            }
        }
    }

    private void MenuItem_CreateFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Extras.InputTextDialog(this, "Создать папку", "Введите имя новой папки:");
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            _viewModel.CreateItemCommand.Execute((dialog.InputText.Trim(), true));
        }
    }

    private void MenuItem_CreateFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Extras.InputTextDialog(this, "Создать файл", "Введите имя нового файла:");
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            _viewModel.CreateItemCommand.Execute((dialog.InputText.Trim(), false));
        }
    }

    // --- ОЧИСТКА РЕСУРСОВ ---

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        TerminalWebView.Dispose();
        base.OnClosed(e);
    }
}