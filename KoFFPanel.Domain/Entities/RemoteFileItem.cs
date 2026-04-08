namespace KoFFPanel.Domain.Entities;

public class RemoteFileItem
{
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }

    // Если это папка - иконка папки (желтая), если файл - иконка документа (серая)
    public string Icon => IsDirectory ? "Folder24" : "Document24";
    public string IconColor => IsDirectory ? "#ffb86c" : "#a0aabf";
}