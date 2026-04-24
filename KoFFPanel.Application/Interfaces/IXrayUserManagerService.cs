using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IXrayUserManagerService
{
    Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp);

    // === СИНХРОНИЗАЦИЯ КОНТРАКТА: Метод обязан возвращать 3 параметра ===
    Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string email, long limit, DateTime? expiry, bool isP2PBlocked = true);

    Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string email);
    Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh);
    Task<bool> ResetTrafficAsync(ISshService ssh, string email);
    Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string email, bool active);
    Task<bool> UpdateUserLimitsAsync(string serverIp, string email, long limit, DateTime? expiry);
    Task SaveTrafficToDbAsync(string ip, IEnumerable<VpnClient> clients);
    Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> dbUsers);
}