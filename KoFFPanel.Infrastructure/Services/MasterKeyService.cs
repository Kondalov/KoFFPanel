using System;
using System.IO;

namespace KoFFPanel.Infrastructure.Services;

public interface IMasterKeyService
{
    string GetMasterPassword();
}

public class MasterKeyService : IMasterKeyService
{
    private static MasterKeyService? _instance;
    public static MasterKeyService Instance => _instance ??= new MasterKeyService();

    private string? _cachedPassword;

    public string GetMasterPassword()
    {
        // 2026 MIGRATION FIX: Используем временный жесткий ключ, чтобы избежать проблем с путями BaseDirectory
        return "KoFF_Fixed_Master_Key_2026_!!_Safe";
    }
}
