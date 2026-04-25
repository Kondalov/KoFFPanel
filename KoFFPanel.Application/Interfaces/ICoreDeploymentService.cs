using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using KoFFPanel.Domain.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface ICoreDeploymentService
{
    Task<(bool IsSuccess, string Message)> RunPreFlightChecksAsync(ISshService ssh);

    Task<string> GetInstalledXrayVersionAsync(ISshService ssh);
    Task<string> GetInstalledSingBoxVersionAsync(ISshService ssh);
    Task<string> GetInstalledTrustTunnelVersionAsync(ISshService ssh);

    Task<(bool IsSuccess, string Log)> InstallXrayAsync(ISshService ssh, string targetVersion = "latest");
    Task<(bool IsSuccess, string Log)> InstallSingBoxAsync(ISshService ssh, string targetVersion = "latest");
    Task<(bool IsSuccess, string Log)> InstallTrustTunnelAsync(ISshService ssh, string targetVersion = "latest");

    // Универсальный метод для любого ядра (Xray, Sing-box, TrustTunnel)
    Task<(bool IsSuccess, string Log)> DeployFullStackAsync(
        ISshService ssh,
        VpnProfile profile,
        string coreType,
        List<(IProtocolBuilder Builder, int Port, string? TtUsername, string? TtPassword)> protocols);
}