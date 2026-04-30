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
        _logger.Log("SING-BOX", "Обновление гео-баз для Sing-box...");
        
        // Sing-box 1.11+ поддерживает классические dat файлы, если они указаны в конфиге, 
        // но лучше скачать их в /var/lib/sing-box/ или аналогичное место.
        string cmd = @"
mkdir -p /var/lib/sing-box
curl -L -s -o /var/lib/sing-box/geoip.db https://github.com/SagerNet/sing-geosite/releases/latest/download/geoip.db
curl -L -s -o /var/lib/sing-box/geosite.db https://github.com/SagerNet/sing-geosite/releases/latest/download/geosite.db
echo 'SUCCESS'
";
        var res = await ssh.ExecuteCommandAsync(cmd);
        if (res.Contains("SUCCESS"))
            return (true, "Базы Sing-box (geoip.db, geosite.db) успешно обновлены.");
            
        return (false, "Ошибка при скачивании баз Sing-box.");
    }
}