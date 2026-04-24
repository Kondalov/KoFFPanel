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

    // === ZERO TRUST: Расшифровка паролей при загрузке ===
    public List<VpnProfile> LoadProfiles()
    {
        if (!File.Exists(_dbFilePath)) return new List<VpnProfile>();
        try
        {
            string json = File.ReadAllText(_dbFilePath);
            var profiles = JsonSerializer.Deserialize<List<VpnProfile>>(json) ?? new List<VpnProfile>();

            foreach (var p in profiles)
            {
                if (!string.IsNullOrEmpty(p.Password) && p.Password.StartsWith("DPAPI:", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        string b64 = p.Password.Substring(6);
                        byte[] encryptedBytes = Convert.FromBase64String(b64);
                        byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, null, DataProtectionScope.CurrentUser);
                        p.Password = System.Text.Encoding.UTF8.GetString(decryptedBytes);
                    }
                    catch
                    {
                        // Если файл перенесли на другой ПК или под другого пользователя Windows
                        p.Password = "";
                    }
                }
            }
            return profiles;
        }
        catch { return new List<VpnProfile>(); }
    }

    // === ZERO TRUST: Шифрование паролей перед записью на диск ===
    public void SaveProfiles(List<VpnProfile> profiles)
    {
        Directory.CreateDirectory(_appDataFolder);

        // УМНЫЙ АЛГОРИТМ: Создаем глубокую копию списка (Клон).
        // Если мы зашифруем пароли напрямую в profiles, они зашифруются в оперативной памяти UI, 
        // и юзер увидит в текстовом поле абракадабру "DPAPI:...".
        var jsonCopy = JsonSerializer.Serialize(profiles);
        var safeProfiles = JsonSerializer.Deserialize<List<VpnProfile>>(jsonCopy) ?? new List<VpnProfile>();

        foreach (var p in safeProfiles)
        {
            if (!string.IsNullOrEmpty(p.Password) && !p.Password.StartsWith("DPAPI:", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    byte[] plainBytes = System.Text.Encoding.UTF8.GetBytes(p.Password);
                    byte[] encryptedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
                    p.Password = "DPAPI:" + Convert.ToBase64String(encryptedBytes);
                }
                catch { }
            }
        }

        string finalJson = JsonSerializer.Serialize(safeProfiles, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_dbFilePath, finalJson);
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