using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface ISingBoxConfiguratorService
{
    Task<(bool IsSuccess, string Message)> UpdateGeoDataAsync(ISshService ssh);
}