using KoFFPanel.Domain.Entities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IAntiFraudService
{
    Task<(bool IsFraud, string Reason)> EvaluateClientAsync(string serverIp, VpnClient client, string currentIp, long trafficDelta, CancellationToken token = default);
    Task<List<ClientBehaviorLog>> GetMonthlyBehaviorAsync(string serverIp, string email, CancellationToken token = default);
    Task ExecuteMonthlyRetentionPolicyAsync(CancellationToken token = default);
}