using KoFFPanel.Domain.Entities;
using System.Collections.Generic;

namespace KoFFPanel.Application.Interfaces;

public interface IProfileRepository
{
    List<VpnProfile> LoadProfiles();
    void SaveProfiles(List<VpnProfile> profiles);
    void AddProfile(VpnProfile profile);
    void UpdateProfile(VpnProfile updatedProfile);
    void DeleteProfile(string id);
}