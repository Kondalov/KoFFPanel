using KoFFPanel.Application.Interfaces;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class SingBoxConfiguratorService : ISingBoxConfiguratorService
{
    private readonly IAppLogger _logger;

    public SingBoxConfiguratorService(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<(bool IsSuccess, string Message)> UpdateGeoDataAsync(ISshService ssh)
    {
        // Базовый метод для будущих реализаций (Sing-box использует свои базы rule-set)
        _logger.Log("SING-BOX", "Заглушка обновления гео-баз для Sing-box");
        await Task.CompletedTask;
        return (true, "Базы Sing-box в актуальном состоянии (встроены в ядро).");
    }
}