using KoFFPanel.Application.Interfaces;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Strategies;

public interface ICoreInstallStrategy
{
    Task<(bool Success, string Message, object? Result)> ExecuteFullInstall(
        ISshService ssh,
        string ipAddress,
        int vpnPort,
        string sni,
        string existingUuid = "",
        string existingPrivKey = "",
        string existingPubKey = "",
        string existingShortId = "",
        string customDomain = "",
        string connectionNode = "");
        }