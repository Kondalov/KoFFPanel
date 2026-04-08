using KoFFPanel.Domain.Entities;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IClientAnalyticsService
{
    // Добавлен список violations в параметры
    Task SaveBatchAsync(string serverIp, Dictionary<string, long> trafficDeltas, List<(string Email, string Ip, string Country)> connections, List<(string Email, string ViolationType)> violations);

    Task<List<ClientTrafficLog>> GetTrafficLogsAsync(string serverIp, string email, int days = 30);
    Task<List<ClientConnectionLog>> GetConnectionLogsAsync(string serverIp, string email);

    // Новый метод получения нарушений
    Task<List<ClientViolationLog>> GetViolationLogsAsync(string serverIp, string email);

    Task CleanupOldLogsAsync();
}