using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface ISubscriptionService
{
    Task<bool> InitializeServerAsync(ISshService ssh);

    // ИСПРАВЛЕНИЕ АРХИТЕКТУРЫ: Принимаем коллекцию ссылок для мультипротокольной подписки
    Task<bool> UpdateUserSubscriptionAsync(ISshService ssh, string uuid, IEnumerable<string> links);
    Task<bool> DeleteUserSubscriptionAsync(ISshService ssh, string uuid);
    string GetSubscriptionUrl(string serverIp, string uuid);
    void SetCustomDomain(string domain);
}