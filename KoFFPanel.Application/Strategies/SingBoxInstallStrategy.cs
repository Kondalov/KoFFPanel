using KoFFPanel.Application.Interfaces;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Strategies;

public class SingBoxInstallStrategy : ICoreInstallStrategy
{
    public async Task<(bool Success, string Message, object? Result)> ExecuteFullInstall(
        ISshService ssh,
        string ipAddress,
        int vpnPort,
        string sni,
        string existingUuid = "",
        string existingPrivKey = "",
        string existingPubKey = "",
        string existingShortId = "")
    {
        return (false, "Стратегия установки Sing-box находится в разработке.", null);
    }
}