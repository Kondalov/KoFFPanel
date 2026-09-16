using KoFFPanel.Application.Interfaces;
using System.Security.Cryptography;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;

namespace KoFFPanel.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public class ProfileRepository : IProfileRepository
{
    private static readonly object _syncLock = new object();
    private readonly string _appDataFolder;
    private readonly string _dbFilePath;

    public ProfileRepository()
    {
        string appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

#if DEBUG
        string configFolderName = "KoFFPanel_Dev";
#else
        string configFolderName = "KoFFPanel";
#endif

        _appDataFolder = Path.Combine(appDataPath, configFolderName);
        _dbFilePath = Path.Combine(_appDataFolder, "ProfilesDB.json");
    }

    public List<VpnProfile> LoadProfiles()
    {
        lock (_syncLock)
        {
            var profiles = new List<VpnProfile>();
            if (File.Exists(_dbFilePath))
            {
                try
                {
                    string json = File.ReadAllText(_dbFilePath);
                    profiles = JsonSerializer.Deserialize<List<VpnProfile>>(json) ?? new List<VpnProfile>();
                }
                catch { }
            }

#if DEBUG
            // Бесшовная синхронизация для Debug-сборок: если сервер настроен в релизном профиле, подтягиваем его
            string releaseDbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoFFPanel", "ProfilesDB.json");
            if (File.Exists(releaseDbPath))
            {
                try
                {
                    string releaseJson = File.ReadAllText(releaseDbPath);
                    var releaseProfiles = JsonSerializer.Deserialize<List<VpnProfile>>(releaseJson) ?? new List<VpnProfile>();
                    foreach (var rp in releaseProfiles)
                    {
                        if (!profiles.Any(p => p.IpAddress == rp.IpAddress || p.Id == rp.Id))
                        {
                            profiles.Add(rp);
                        }
                    }
                }
                catch { }
            }
#endif

            if (profiles.Count == 0) return profiles;

            try
            {
                string masterKey = MasterKeyService.Instance.GetMasterPassword();

                foreach (var p in profiles)
                {
                    if (string.IsNullOrEmpty(p.Password)) continue;

                    if (p.Password.StartsWith("AESGCM:", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            p.Password = DecryptAesGcm(p.Password.Substring(7), masterKey);
                        }
                        catch
                        {
                            // Preserve original ciphertext on failure to prevent silent data loss
                        }
                    }
                    else if (p.Password.StartsWith("AES:", StringComparison.OrdinalIgnoreCase))
                    {
                        string cipher = p.Password.Substring(4);
                        try
                        {
                            // Try decrypting with current master key first
                            p.Password = DecryptLegacyAesCbc(cipher, masterKey);
                        }
                        catch
                        {
                            try
                            {
                                // Try decrypting with legacy static master key for migration
                                p.Password = DecryptLegacyAesCbc(cipher, MasterKeyService.LegacyMasterPassword);
                            }
                            catch
                            {
                                // Keep original password ciphertext; do NOT erase to empty string
                            }
                        }
                    }
                    else if (p.Password.StartsWith("DPAPI:", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            byte[] protectedBytes = Convert.FromBase64String(p.Password.Substring(6));
                            byte[] rawBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
                            p.Password = Encoding.UTF8.GetString(rawBytes);
                        }
                        catch
                        {
                            // Keep original password ciphertext; do NOT erase
                        }
                    }
                }
                return profiles;
            }
            catch { return new List<VpnProfile>(); }
        }
    }

    public void SaveProfiles(List<VpnProfile> profiles)
    {
        lock (_syncLock)
        {
            Directory.CreateDirectory(_appDataFolder);

            // Create backup of old profiles before overwriting
            if (File.Exists(_dbFilePath))
            {
                try
                {
                    File.Copy(_dbFilePath, _dbFilePath + ".bak", true);
                }
                catch { }
            }

            var jsonCopy = JsonSerializer.Serialize(profiles);
            var safeProfiles = JsonSerializer.Deserialize<List<VpnProfile>>(jsonCopy) ?? new List<VpnProfile>();

            string masterKey = MasterKeyService.Instance.GetMasterPassword();

            foreach (var p in safeProfiles)
            {
                if (!string.IsNullOrEmpty(p.Password) && !p.Password.StartsWith("AESGCM:", StringComparison.OrdinalIgnoreCase))
                {
                    // If it was another format or plain text, encrypt using AesGcm
                    try
                    {
                        p.Password = "AESGCM:" + EncryptAesGcm(p.Password, masterKey);
                    }
                    catch { }
                }
            }

            string finalJson = JsonSerializer.Serialize(safeProfiles, new JsonSerializerOptions { WriteIndented = true });

            string tempFilePath = _dbFilePath + ".tmp";
            File.WriteAllText(tempFilePath, finalJson);
            File.Move(tempFilePath, _dbFilePath, true);
        }
    }

    internal static string EncryptAesGcm(string text, string key)
    {
        byte[] keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        byte[] nonce = new byte[AesGcm.NonceByteSizes.MaxSize]; // 12 bytes
        RandomNumberGenerator.Fill(nonce);

        byte[] plainBytes = Encoding.UTF8.GetBytes(text);
        byte[] cipherBytes = new byte[plainBytes.Length];
        byte[] tag = new byte[AesGcm.TagByteSizes.MaxSize]; // 16 bytes

        using var aes = new AesGcm(keyBytes, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        byte[] result = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, result, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, result, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, result, nonce.Length + tag.Length, cipherBytes.Length);

        return Convert.ToBase64String(result);
    }

    internal static string DecryptAesGcm(string cipherText, string key)
    {
        byte[] fullPayload = Convert.FromBase64String(cipherText);
        byte[] keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        int nonceSize = AesGcm.NonceByteSizes.MaxSize;
        int tagSize = AesGcm.TagByteSizes.MaxSize;

        if (fullPayload.Length < nonceSize + tagSize)
            throw new CryptographicException("Ciphertext payload too short.");

        byte[] nonce = new byte[nonceSize];
        byte[] tag = new byte[tagSize];
        byte[] cipherBytes = new byte[fullPayload.Length - nonceSize - tagSize];

        Buffer.BlockCopy(fullPayload, 0, nonce, 0, nonceSize);
        Buffer.BlockCopy(fullPayload, nonceSize, tag, 0, tagSize);
        Buffer.BlockCopy(fullPayload, nonceSize + tagSize, cipherBytes, 0, cipherBytes.Length);

        byte[] plainBytes = new byte[cipherBytes.Length];
        using var aes = new AesGcm(keyBytes, tagSize);
        aes.Decrypt(nonce, cipherBytes, tag, plainBytes);

        return Encoding.UTF8.GetString(plainBytes);
    }

    internal static string DecryptLegacyAesCbc(string cipherText, string key)
    {
        byte[] fullCipher = Convert.FromBase64String(cipherText);
        using var aes = Aes.Create();
        var keyBytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        aes.Key = keyBytes;

        byte[] iv = new byte[aes.BlockSize / 8];
        Array.Copy(fullCipher, 0, iv, 0, iv.Length);
        aes.IV = iv;

        using var decryptor = aes.CreateDecryptor();
        using var ms = new MemoryStream(fullCipher, iv.Length, fullCipher.Length - iv.Length);
        using var cs = new CryptoStream(ms, decryptor, CryptoStreamMode.Read);
        using var sr = new StreamReader(cs);
        return sr.ReadToEnd();
    }

    public void AddProfile(VpnProfile profile)
    {
        lock (_syncLock)
        {
            var profiles = LoadProfiles();
            if (profiles.Any(p => p.Id == profile.Id))
            {
                var existing = profiles.First(p => p.Id == profile.Id);
                existing.Name = profile.Name;
                existing.IpAddress = profile.IpAddress;
                existing.Port = profile.Port;
                existing.Username = profile.Username;
                existing.Password = profile.Password;
                existing.KeyPath = profile.KeyPath;
                existing.Inbounds = profile.Inbounds;
                existing.CoreType = profile.CoreType;
                existing.CustomDomain = profile.CustomDomain;
                existing.ConnectionNode = profile.ConnectionNode;
                existing.SshHostKeyFingerprint = profile.SshHostKeyFingerprint;
            }
            else
            {
                profiles.Add(profile);
            }
            SaveProfiles(profiles);
        }
    }

    public void UpdateProfile(VpnProfile profile)
    {
        AddProfile(profile);
    }

    public void DeleteProfile(string profileId)
    {
        lock (_syncLock)
        {
            var profiles = LoadProfiles();
            profiles.RemoveAll(p => p.Id == profileId);
            SaveProfiles(profiles);
        }
    }
}