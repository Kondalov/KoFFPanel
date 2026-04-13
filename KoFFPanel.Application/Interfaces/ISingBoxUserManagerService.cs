using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface ISingBoxUserManagerService
{
    Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp);

    // ИСПРАВЛЕНИЕ: Добавлен параметр isP2PBlocked
    Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string name, long trafficLimitBytes, DateTime? expiryDate, bool isP2PBlocked = true);

    Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string name);
    Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string name, bool enableAccess);

    // ИСПРАВЛЕНИЕ: Добавлен ISshService для пересборки конфига и флаг P2P
    Task<bool> UpdateUserLimitsAsync(ISshService ssh, string serverIp, string name, long newLimitBytes, DateTime? newExpiryDate, bool isP2PBlocked = true);

    Task SaveTrafficToDbAsync(string serverIp, IEnumerable<VpnClient> clients);
    Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh);
    Task<bool> ResetTrafficAsync(ISshService ssh, string name);
    Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> dbUsers);
}