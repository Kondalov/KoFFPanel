using KoFFPanel.Domain.Entities;
using KoFFPanel.Application.Interfaces;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces.ProtocolBuilders;

public interface IProtocolBuilder
{
    string ProtocolType { get; }      // Внутреннее имя (vless, hysteria2, trusttunnel)
    string DisplayName { get; }       // Имя для UI в Мастере установки
    string TransportType { get; }     // Транспорт "tcp" или "udp" (очень важно для проверки портов!)
    int DefaultPort { get; }          // Порт, который умный алгоритм предложит первым

    // Генерация ключей на сервере и создание объекта Inbound для сохранения в БД
    Task<ServerInbound> GenerateNewInboundAsync(ISshService ssh, int port);

    // Сборка красивой клиентской ссылки для конкретного юзера
    string GenerateClientLink(ServerInbound inbound, string serverIp, string clientUuid, string clientEmail);
}