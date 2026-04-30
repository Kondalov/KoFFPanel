using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using KoFFPanel.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class MarzbanMigrationService : IMarzbanMigrationService
{
    private readonly AppDbContext _dbContext;
    private readonly IAppLogger _logger;

    public MarzbanMigrationService(AppDbContext dbContext, IAppLogger logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<(bool IsSuccess, string Message)> MigrateAsync(string marzbanSqlPath, string botSqlPath, string serverIp)
    {
        try
        {
            _logger.Log("MIGRATION", "Запуск глубокой миграции с восстановлением рефералов...");

            if (!File.Exists(marzbanSqlPath)) return (false, $"Файл не найден: {marzbanSqlPath}");
            if (!File.Exists(botSqlPath)) return (false, $"Файл не найден: {botSqlPath}");

            var marzbanContent = await File.ReadAllLinesAsync(marzbanSqlPath, Encoding.UTF8);
            var botContent = await File.ReadAllLinesAsync(botSqlPath, Encoding.UTF8);

            // 1. Парсим пользователей Marzban
            var marzbanUsers = ParseMarzbanUsers(marzbanContent);
            _logger.Log("MIGRATION", $"Найдено пользователей в Marzban: {marzbanUsers.Count}");

            // 2. Парсим прокси (UUID)
            var userUuids = ParseMarzbanProxies(marzbanContent);

            // 3. Парсим данные старого бота (Сроки + Рефералы)
            var botUserData = ParseBotUserData(botContent);
            _logger.Log("MIGRATION", $"Найдено связей в базе бота: {botUserData.Count}");

            int importedCount = 0;
            int skippedCount = 0;
            int refCount = 0;

            foreach (var mUser in marzbanUsers)
            {
                string uuid = userUuids.GetValueOrDefault(mUser.Id, Guid.NewGuid().ToString());
                
                if (await _dbContext.Clients.AnyAsync(c => c.Email == mUser.Username || c.Uuid == uuid))
                {
                    skippedCount++;
                    continue;
                }

                // Извлекаем Telegram ID из username (user_12345678)
                long telegramId = 0;
                var tgMatch = Regex.Match(mUser.Username, @"\d+");
                if (tgMatch.Success) long.TryParse(tgMatch.Value, out telegramId);

                DateTime? expiry = null;
                long? referrerId = null;

                if (telegramId > 0 && botUserData.TryGetValue(telegramId, out var botInfo))
                {
                    if (botInfo.ExpiryDate > DateTime.Now && botInfo.ExpiryDate < DateTime.Now.AddYears(100))
                        expiry = botInfo.ExpiryDate;
                    
                    if (botInfo.ReferrerId > 0)
                    {
                        referrerId = botInfo.ReferrerId;
                        refCount++;
                    }
                }

                var client = new VpnClient
                {
                    ServerIp = serverIp,
                    Email = mUser.Username,
                    Uuid = uuid,
                    TrafficUsed = mUser.UsedTraffic,
                    TrafficLimit = mUser.DataLimit,
                    ExpiryDate = expiry,
                    ReffererId = referrerId, // СОХРАНЯЕМ РЕФЕРАЛА!
                    IsActive = mUser.Status == "active",
                    Note = $"[MIGRATED] {mUser.Note}".Trim(),
                    Protocol = "VLESS",
                    IsVlessEnabled = true,
                    IsAntiFraudEnabled = true,
                    IsP2PBlocked = true
                };

                _dbContext.Clients.Add(client);
                importedCount++;
            }

            await _dbContext.SaveChangesAsync();

            string report = $"Миграция завершена! Импортировано: {importedCount}, Восстановлено рефералов: {refCount}, Пропущено: {skippedCount}.";
            _logger.Log("MIGRATION-SUCCESS", report);
            return (true, report);
        }
        catch (Exception ex)
        {
            _logger.Log("MIGRATION-ERROR", $"Ошибка при миграции: {ex.Message}");
            return (false, $"Ошибка: {ex.Message}");
        }
    }

    private List<MarzbanUserRaw> ParseMarzbanUsers(string[] lines)
    {
        var users = new List<MarzbanUserRaw>();
        var regex = new Regex(@"INSERT INTO users VALUES\((?<id>\d+),'(?<username>[^']+)',\s*'(?<status>[^']+)',\s*(?<used>\d+|NULL),\s*(?<limit>\d+|NULL),\s*(?<expire>\d+|NULL),[^,]+,[^,]+,[^,]+,[^,]+,(?:'(?<note>[^']*)'|NULL)", RegexOptions.Compiled);

        foreach (var line in lines)
        {
            var match = regex.Match(line);
            if (match.Success)
            {
                users.Add(new MarzbanUserRaw
                {
                    Id = int.Parse(match.Groups["id"].Value),
                    Username = match.Groups["username"].Value,
                    Status = match.Groups["status"].Value,
                    UsedTraffic = long.TryParse(match.Groups["used"].Value, out var u) ? u : 0,
                    DataLimit = long.TryParse(match.Groups["limit"].Value, out var l) ? l : 0,
                    Note = match.Groups["note"].Value
                });
            }
        }
        return users;
    }

    private Dictionary<int, string> ParseMarzbanProxies(string[] lines)
    {
        var dict = new Dictionary<int, string>();
        var regex = new Regex(@"INSERT INTO proxies VALUES\(\d+,(?<userId>\d+),'VLESS','(?<json>.+)'\);", RegexOptions.Compiled);

        foreach (var line in lines)
        {
            var match = regex.Match(line);
            if (match.Success)
            {
                try
                {
                    int userId = int.Parse(match.Groups["userId"].Value);
                    string jsonStr = match.Groups["json"].Value.Replace("''", "'");
                    var node = JsonNode.Parse(jsonStr);
                    string uuid = node?["id"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(uuid)) dict[userId] = uuid;
                }
                catch { }
            }
        }
        return dict;
    }

    private Dictionary<long, BotUserRaw> ParseBotUserData(string[] lines)
    {
        var dict = new Dictionary<long, BotUserRaw>();
        
        // 1. Парсим сроки подписок
        var subRegex = new Regex(@"INSERT INTO Subscriptions VALUES\(\d+,(?<tgId>\d+),'(?<expiry>[^']+)'", RegexOptions.Compiled);
        foreach (var line in lines)
        {
            var match = subRegex.Match(line);
            if (match.Success && long.TryParse(match.Groups["tgId"].Value, out var tgId))
            {
                if (DateTime.TryParse(match.Groups["expiry"].Value, out var expiry))
                {
                    if (!dict.TryGetValue(tgId, out var existing)) existing = new BotUserRaw { TelegramId = tgId };
                    dict[tgId] = existing with { ExpiryDate = expiry };
                }
            }
        }

        // 2. Парсим рефералов из таблицы Users
        var userRegex = new Regex(@"INSERT INTO Users VALUES\(\d+,(?<tgId>\d+),(?:'[^']*'|NULL),(?<refId>\d+|NULL)", RegexOptions.Compiled);
        foreach (var line in lines)
        {
            var match = userRegex.Match(line);
            if (match.Success && long.TryParse(match.Groups["tgId"].Value, out var tgId))
            {
                long? refId = long.TryParse(match.Groups["refId"].Value, out var r) ? r : null;
                if (refId == 0) refId = null;

                if (!dict.TryGetValue(tgId, out var existing)) existing = new BotUserRaw { TelegramId = tgId };
                dict[tgId] = existing with { ReferrerId = refId };
            }
        }

        return dict;
    }

    private record MarzbanUserRaw
    {
        public int Id { get; init; }
        public string Username { get; init; } = "";
        public string Status { get; init; } = "";
        public long UsedTraffic { get; init; }
        public long DataLimit { get; init; }
        public string Note { get; init; } = "";
    }

    private record BotUserRaw
    {
        public long TelegramId { get; init; }
        public DateTime ExpiryDate { get; init; }
        public long? ReferrerId { get; init; }
    }
}
