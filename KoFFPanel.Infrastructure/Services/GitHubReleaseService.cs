using KoFFPanel.Application.Interfaces;
using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class GitHubReleaseService : IGitHubReleaseService
{
    private readonly HttpClient _httpClient;

    // В .NET 10 HttpClient инжектится через IHttpClientFactory под капотом
    public GitHubReleaseService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        // GitHub API требует наличие User-Agent, иначе вернет ошибку 403
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "KoFFPanel-AutoUpdater");
    }

    public async Task<string> GetLatestReleaseVersionAsync(string repositoryPath)
    {
        try
        {
            var url = $"https://api.github.com/repos/{repositoryPath}/releases/latest";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode) return "Ошибка сети";

            var jsonDocument = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            return jsonDocument.RootElement.GetProperty("tag_name").GetString() ?? "Неизвестно";
        }
        catch
        {
            return "Ошибка API";
        }
    }
}