namespace KoFFPanel.Application.Interfaces;

public interface IFilePickerService
{
    string? PickSshKeyFile();
    string? SaveFile(string defaultName, string filter);
}