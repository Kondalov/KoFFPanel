using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface ICoreDeploymentService
{
    // Умная проверка сервера перед любыми действиями (защита от дурака)
    Task<(bool IsSuccess, string Message)> RunPreFlightChecksAsync(ISshService ssh);

    Task<string> GetInstalledXrayVersionAsync(ISshService ssh);
    Task<string> GetInstalledSingBoxVersionAsync(ISshService ssh);

    // Методы установки с возвратом лога установки
    Task<(bool IsSuccess, string Log)> InstallXrayAsync(ISshService ssh, string targetVersion = "latest");
    Task<(bool IsSuccess, string Log)> InstallSingBoxAsync(ISshService ssh, string targetVersion = "latest");
}