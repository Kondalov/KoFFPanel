using KoFFPanel.Application.Interfaces;
using Microsoft.Win32;

namespace KoFFPanel.Presentation.Services;

public class FilePickerService : IFilePickerService
{
    public string? PickSshKeyFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите файл приватного SSH-ключа",
            Filter = "SSH Ключи (*.pem;*.ppk;*.key;id_rsa)|*.pem;*.ppk;*.key;id_rsa|Все файлы (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            return dialog.FileName;
        }

        return null;
    }

    public string? SaveFile(string defaultName, string filter)
    {
        var dialog = new SaveFileDialog
        {
            FileName = defaultName,
            Filter = filter,
            Title = "Сохранить файл сертификата"
        };

        if (dialog.ShowDialog() == true)
        {
            return dialog.FileName;
        }

        return null;
    }
}
