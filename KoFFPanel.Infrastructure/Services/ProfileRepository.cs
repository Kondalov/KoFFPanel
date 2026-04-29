using KoFFPanel.Application.Interfaces;
using System.Security.Cryptography;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Runtime.Versioning;

namespace KoFFPanel.Infrastructure.Services;

[SupportedOSPlatform("windows")]
public class ProfileRepository : IProfileRepository
{
    private readonly string _appDataFolder;
    private readonly string _dbFilePath;

    public ProfileRepository()
    {
        _appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoFFPanel");
        _dbFilePath = Path.Combine(_appDataFolder, "ProfilesDB.json");
    }

    // === 2026 MODERNIZATION: Расшифровка паролей через Master Password (Портативно) ===
    public List<VpnProfile> LoadProfiles()
    {
        if (!File.Exists(_dbFilePath)) return new List<VpnProfile>();
        try
        {
            string json = File.ReadAllText(_dbFilePath);
            var profiles = JsonSerializer.Deserialize<List<VpnProfile>>(json) ?? new List<VpnProfile>();

            string masterKey = MasterKeyService.Instance.GetMasterPassword();

            foreach (var p in profiles)
            {
                if (string.IsNullOrEmpty(p.Password)) continue;

                if (p.Password.StartsWith("AES:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        p.Password = DecryptString(p.Password.Substring(4), masterKey);
                    }
                    catch { p.Password = ""; }
                }
                else if (p.Password.StartsWith("DPAPI:", StringComparison.OrdinalIgnoreCase))
                {
                    // Оставляем пустой пароль для старых DPAPI записей, т.к. мы ушли от привязки к Windows
                    p.Password = "";
                }
            }
            return profiles;
        }
        catch { return new List<VpnProfile>(); }
    }

    // === 2026 MODERNIZATION: Шифрование через Master Password ===
    public void SaveProfiles(List<VpnProfile> profiles)
    {
        Directory.CreateDirectory(_appDataFolder);

        var jsonCopy = JsonSerializer.Serialize(profiles);
        var safeProfiles = JsonSerializer.Deserialize<List<VpnProfile>>(jsonCopy) ?? new List<VpnProfile>();

        string masterKey = MasterKeyService.Instance.GetMasterPassword();

        foreach (var p in safeProfiles)
        {
            if (!string.IsNullOrEmpty(p.Password) && !p.Password.StartsWith("AES:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    p.Password = "AES:" + EncryptString(p.Password, masterKey);
                }
                catch { }
            }
        }

        string finalJson = JsonSerializer.Serialize(safeProfiles, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_dbFilePath, finalJson);
    }

    private string EncryptString(string text, string key)
    {
        // В 2026 используем простой и надежный AES для локальных паролей
        using var aes = Aes.Create();
        var keyBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
        aes.Key = keyBytes;
        aes.GenerateIV();

        using var encryptor = aes.CreateEncryptor();
        using var ms = new MemoryStream();
        ms.Write(aes.IV, 0, aes.IV.Length);
        using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
        using (var sw = new StreamWriter(cs))
        {
            sw.Write(text);
        }
        return Convert.ToBase64String(ms.ToArray());
    }

    private string DecryptString(string cipherText, string key)
    {
        byte[] fullCipher = Convert.FromBase64String(cipherText);
        using var aes = Aes.Create();
        var keyBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));
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
        var profiles = LoadProfiles();
        profiles.Add(profile);
        SaveProfiles(profiles);
    }

    public void UpdateProfile(VpnProfile updatedProfile)
    {
        var profiles = LoadProfiles();
        var index = profiles.FindIndex(p => p.Id == updatedProfile.Id);

        if (index >= 0)
        {
            profiles[index] = updatedProfile;
        }
        else
        {
            profiles.Add(updatedProfile);
        }

        SaveProfiles(profiles);
    }

    public void DeleteProfile(string id)
    {
        var profiles = LoadProfiles();
        var profileToRemove = profiles.FirstOrDefault(p => p.Id == id);
        if (profileToRemove != null)
        {
            profiles.Remove(profileToRemove);
            SaveProfiles(profiles);
        }
    }
}