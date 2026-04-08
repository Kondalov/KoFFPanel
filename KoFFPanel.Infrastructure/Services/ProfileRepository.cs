using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace KoFFPanel.Infrastructure.Services;

public class ProfileRepository : IProfileRepository
{
    private readonly string _appDataFolder;
    private readonly string _dbFilePath;

    public ProfileRepository()
    {
        _appDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoFFPanel");
        _dbFilePath = Path.Combine(_appDataFolder, "ProfilesDB.json");
    }

    public List<VpnProfile> LoadProfiles()
    {
        if (!File.Exists(_dbFilePath)) return new List<VpnProfile>();
        try
        {
            string json = File.ReadAllText(_dbFilePath);
            return JsonSerializer.Deserialize<List<VpnProfile>>(json) ?? new List<VpnProfile>();
        }
        catch { return new List<VpnProfile>(); }
    }

    public void SaveProfiles(List<VpnProfile> profiles)
    {
        Directory.CreateDirectory(_appDataFolder);
        string json = JsonSerializer.Serialize(profiles, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(_dbFilePath, json);
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