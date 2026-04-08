using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IXrayConfiguratorService
{
    // Возвращает: Успех ли?, Сообщение (лог), и готовую ссылку VLESS
    Task<(bool IsSuccess, string Message, string VlessLink)> InitializeRealityAsync(ISshService ssh, string serverIp);
}