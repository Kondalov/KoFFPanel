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
        if (_cachedPassword != null) return _cachedPassword;

        string masterKeyPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "MasterPassword_DO_NOT_SHARE.txt");
        try
        {
            if (File.Exists(masterKeyPath))
            {
                _cachedPassword = File.ReadAllText(masterKeyPath).Trim();
            }
            else
            {
                byte[] randomBytes = new byte[24];
                using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create())
                {
                    rng.GetBytes(randomBytes);
                }
                _cachedPassword = Convert.ToBase64String(randomBytes);
                File.WriteAllText(masterKeyPath, _cachedPassword);
                File.SetAttributes(masterKeyPath, FileAttributes.Hidden);
            }
            return _cachedPassword;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MASTER-KEY-ERROR] {ex.Message}");
            return "koff_emergency_recovery_key_2026_fallback!";
        }
    }
}