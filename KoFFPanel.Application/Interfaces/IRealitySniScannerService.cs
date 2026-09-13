using KoFFPanel.Domain.Entities;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IRealitySniScannerService
{
    Task<(bool IsAccessible, long LatencyMs, string Error)> CheckSniAsync(string sni, CancellationToken token = default);
    Task<(string BestSni, long LatencyMs)> FindBestSniAsync(IEnumerable<string>? candidatePool = null, CancellationToken token = default);
    Task<(bool Success, string Message)> RotateSniAsync(ISshService ssh, VpnProfile profile, string newSni);
}
