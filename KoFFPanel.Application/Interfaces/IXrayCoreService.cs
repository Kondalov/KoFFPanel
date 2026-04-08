using KoFFPanel.Domain.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IXrayCoreService
{
    Task<string> GetCoreStatusAsync(ISshService ssh);
    Task<string> GetCoreLogsAsync(ISshService ssh, int lines = 50);
    Task<List<VpnClient>> GetClientsAsync(ISshService ssh);
    Task<bool> RestartCoreAsync(ISshService ssh);
    Task RebootServerAsync(ISshService sshService);
}