using KoFFPanel.Application.Interfaces;
using Microsoft.Win32;

namespace KoFFPanel.Presentation.Services;

public class FilePickerService : IFilePickerService
{
    public string? PickSshKeyFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "???????? ????????? SSH-????",
            Filter = "SSH ????? (*.pem;*.ppk;*.key;id_rsa)|*.pem;*.ppk;*.key;id_rsa|??? ????? (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog() == true)
        {
            return dialog.FileName;
        }

        return null;
    }
}
