using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IXrayUserManagerService
{
    Task<List<VpnClient>> GetUsersAsync(ISshService ssh, string serverIp);

    // ИСПРАВЛЕНИЕ: Добавлены параметры isTrojan и isShadowsocks
    Task<(bool IsSuccess, string Message, string VlessLink)> AddUserAsync(ISshService ssh, string serverIp, string email, long limit, DateTime? expiry, bool isP2PBlocked = true, bool isVless = true, bool isHy2 = false, bool isTt = false, bool isTrojan = false, bool isShadowsocks = false);

    Task<(bool IsSuccess, string Message)> RemoveUserAsync(ISshService ssh, string serverIp, string email);
    Task<Dictionary<string, long>> GetTrafficStatsAsync(ISshService ssh);
    Task<bool> ResetTrafficAsync(ISshService ssh, string email);
    Task<(bool IsSuccess, string Message)> ToggleUserStatusAsync(ISshService ssh, string serverIp, string email, bool active);

    // ИСПРАВЛЕНИЕ: Добавлены параметры isTrojan и isShadowsocks
    Task<bool> UpdateUserLimitsAsync(ISshService ssh, string serverIp, string email, long limit, DateTime? expiry, string note, bool isP2PBlocked = true, bool isVless = true, bool isHy2 = false, bool isTt = false, bool isTrojan = false, bool isShadowsocks = false);

    Task SaveTrafficToDbAsync(string ip, IEnumerable<VpnClient> clients);
    Task<bool> SyncUsersToCoreAsync(ISshService ssh, IEnumerable<VpnClient> dbUsers);
}