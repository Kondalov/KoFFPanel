using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IXrayConfiguratorService
{
    Task<(bool IsSuccess, string Message, string VlessLink)> InitializeRealityAsync(ISshService ssh, string serverIp);
}