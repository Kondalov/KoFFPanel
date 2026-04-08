using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IXrayUserManagerService
{
    Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp);

    // Вот правильная сигнатура с 5 параметрами:
    Task<(bool IsSuccess, string Message)> AddUserAsync(ISshService ssh, string serverIp, string email, long trafficLimitBytes, DateTime? expiryDate);

    Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string email);

    Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh);
    Task<bool> ResetTrafficAsync(ISshService ssh, string email);
    Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string email, bool enableAccess);
    Task<bool> UpdateUserLimitsAsync(string serverIp, string email, long newLimitBytes, DateTime? newExpiryDate);
    Task SaveTrafficToDbAsync(string serverIp, IEnumerable<VpnClient> clients);
}