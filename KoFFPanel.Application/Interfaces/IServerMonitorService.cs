using KoFFPanel.Domain.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IServerMonitorService
{
    Task<ServerResources> GetResourcesAsync(ISshService sshService, string coreType);

    Task<List<UserOnlineInfo>> GetUserOnlineStatsAsync(ISshService sshService, string coreType);

    Task<(bool Success, long RoundtripTime)> PingServerAsync(string ip, int timeoutMs = 2000);

    Task<CoreStatusInfo> GetCoreStatusInfoAsync(ISshService sshService, string coreType);
    }