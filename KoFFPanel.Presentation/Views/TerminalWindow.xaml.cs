using KoFFPanel.Domain.Entities;
using KoFFPanel.Presentation.ViewModels;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static KoFFPanel.Presentation.ViewModels.TerminalViewModel;

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
        // ИСПРАВЛЕНИЕ: Передаем _sshService в редактор для умной проверки
        var editorVm = new EditorViewModel(_viewModel.Logger, _viewModel.SshService); // Тебе нужно будет открыть SshService в TerminalViewModel (сделать его public)
        editorVm.Initialize(localPath, remotePath);

        // ИСПРАВЛЕНИЕ: Делегат теперь возвращает Task<bool>
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

    // === ОБРАБОТЧИКИ СНИППЕТОВ ===
    private void AddSnippetCategory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Extras.InputTextDialog(this, "Новая категория", "Введите название (например: Docker):");
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            _viewModel.AddSnippetCategory(dialog.InputText.Trim());
        }
    }

    private void AddSnippetSubCategory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Extras.InputTextDialog(this, "Новая подкатегория", "Введите название (например: Логи):");
        if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
        {
            _viewModel.AddSnippetSubCategory(dialog.InputText.Trim());
        }
    }

    private void AddSnippet_Click(object sender, RoutedEventArgs e)
    {
        SaveNewSnippet();
    }

    private void NewSnippetCmd_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveNewSnippet();
        }
    }

    private void SaveNewSnippet()
    {
        if (!string.IsNullOrWhiteSpace(NewSnippetCmd.Text))
        {
            _viewModel.AddSnippet(NewSnippetDesc.Text, NewSnippetCmd.Text);
            NewSnippetDesc.Text = "";
            NewSnippetCmd.Text = "";
        }
    }

    // === УМНЫЙ НЕВИДИМЫЙ СКРОЛЛ ДЛЯ ВКЛАДОК ===
    private void TabsScrollViewer_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is ScrollViewer scroller)
        {
            // Перехватываем вертикальную прокрутку колесика и превращаем её в горизонтальную
            if (e.Delta > 0)
                scroller.LineLeft();
            else
                scroller.LineRight();

            e.Handled = true; // Отключаем стандартное поведение, чтобы не дергалось всё окно
        }
    }

    // === КОНТЕКСТНОЕ МЕНЮ ДЛЯ ГЛАВНЫХ КАТЕГОРИЙ ===
    private void Category_Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is SnippetCategory cat)
        {
            var dialog = new Extras.InputTextDialog(this, "Переименовать", "Новое название категории:", cat.Name);
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                cat.Name = dialog.InputText.Trim();
                _viewModel.SaveSnippets();
            }
        }
    }

    private void Category_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is SnippetCategory cat)
        {
            var result = MessageBox.Show(this, $"Удалить категорию '{cat.Name}' и все её сниппеты?", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                _viewModel.DeleteCategoryCommand.Execute(cat);
            }
        }
    }

    // === КОНТЕКСТНОЕ МЕНЮ ДЛЯ ПОДКАТЕГОРИЙ ===
    private void SubCategory_Rename_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is SnippetSubCategory sub)
        {
            var dialog = new Extras.InputTextDialog(this, "Переименовать", "Новое название подкатегории:", sub.Name);
            if (dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputText))
            {
                sub.Name = dialog.InputText.Trim();
                _viewModel.SaveSnippets();
            }
        }
    }

    private void SubCategory_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem menuItem && menuItem.DataContext is SnippetSubCategory sub)
        {
            var result = MessageBox.Show(this, $"Удалить подкатегорию '{sub.Name}' и её сниппеты?", "Удаление", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result == MessageBoxResult.Yes)
            {
                if (_viewModel.SelectedSnippetCategory != null)
                {
                    _viewModel.SelectedSnippetCategory.SubCategories.Remove(sub);
                    if (_viewModel.SelectedSnippetSubCategory == sub)
                        _viewModel.SelectedSnippetSubCategory = _viewModel.SelectedSnippetCategory.SubCategories.FirstOrDefault();

                    _viewModel.SaveSnippets();
                }
            }
        }
    }
}