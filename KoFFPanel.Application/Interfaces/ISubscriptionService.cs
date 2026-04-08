using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface ISubscriptionService
{
    Task<bool> InitializeServerAsync(ISshService ssh);

    // ИСПРАВЛЕНИЕ: Меняем email на uuid во всех методах
    Task<bool> UpdateUserSubscriptionAsync(ISshService ssh, string uuid, string vlessLink);
    Task<bool> DeleteUserSubscriptionAsync(ISshService ssh, string uuid);
    string GetSubscriptionUrl(string serverIp, string uuid);
}