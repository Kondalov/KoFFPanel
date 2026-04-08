using System.Threading.Tasks;

namespace KoFFPanel.Application.Interfaces;

public interface IGitHubReleaseService
{
    // Получает последнюю версию (tag) из репозитория (например, "XTLS/Xray-core")
    Task<string> GetLatestReleaseVersionAsync(string repositoryPath);
}