using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Runtime.Versioning;

namespace KoFFPanel.Infrastructure.Services;

public interface IMasterKeyService
{
    string GetMasterPassword();
}

[SupportedOSPlatform("windows")]
public class MasterKeyService : IMasterKeyService
{
    public const string LegacyMasterPassword = "KoFF_Fixed_Master_Key_2026_!!_Safe";

    private static MasterKeyService? _instance;
    public static MasterKeyService Instance => _instance ??= new MasterKeyService();

    private readonly string _keyFilePath;
    private string? _cachedMasterKey;
    private readonly object _lock = new();

    public MasterKeyService()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
#if DEBUG
        string folder = "KoFFPanel_Dev";
#else
        string folder = "KoFFPanel";
#endif
        string dir = Path.Combine(appData, folder);
        Directory.CreateDirectory(dir);
        _keyFilePath = Path.Combine(dir, "koff_master.dpapi");
    }

    public string GetMasterPassword()
    {
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(_cachedMasterKey))
                return _cachedMasterKey;

            if (File.Exists(_keyFilePath))
            {
                try
                {
                    byte[] protectedBytes = File.ReadAllBytes(_keyFilePath);
                    byte[] rawBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                    _cachedMasterKey = Convert.ToBase64String(rawBytes);
                    return _cachedMasterKey;
                }
                catch
                {
                    // Fallback to regeneration if DPAPI payload is corrupted
                }
            }

            // Generate cryptographically secure 256-bit key
            byte[] keyBytes = new byte[32];
            RandomNumberGenerator.Fill(keyBytes);

            byte[] protectedData = ProtectedData.Protect(keyBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_keyFilePath, protectedData);

            _cachedMasterKey = Convert.ToBase64String(keyBytes);
            return _cachedMasterKey;
        }
    }
}
