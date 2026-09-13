using KoFFPanel.Domain.Entities;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IAcmeCertificateService
{
    Task<(bool IsSuccess, string CertPath, string KeyPath, string Message)> EnsureCertificateAsync(ISshService ssh, string serverIp, string? customDomain);
}
