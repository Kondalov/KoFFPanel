using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface ISingBoxUserManagerService
{
    Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp);
    Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string name, long trafficLimitBytes, DateTime? expiryDate);
    Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string name);
    Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string name, bool enableAccess);
    Task<bool> UpdateUserLimitsAsync(string serverIp, string name, long newLimitBytes, DateTime? newExpiryDate);
    Task SaveTrafficToDbAsync(string serverIp, IEnumerable<VpnClient> clients);
    Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh);
    Task<bool> ResetTrafficAsync(ISshService ssh, string name);
    Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> dbUsers);
}