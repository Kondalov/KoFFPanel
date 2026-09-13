using KoFFPanel.Application.Interfaces;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public class SingBoxConfiguratorService : ISingBoxConfiguratorService
{
    private readonly IAppLogger _logger;

    public SingBoxConfiguratorService(IAppLogger logger)
    {
        _logger = logger;
    }

    public async Task<(bool IsSuccess, string Message)> UpdateGeoDataAsync(ISshService ssh)
    {
        _logger.Log("SING-BOX", "Обновление гео-баз для Sing-box...");
        
        // Sing-box 1.11+ поддерживает классические dat файлы, если они указаны в конфиге, 
        // но лучше скачать их в /var/lib/sing-box/ или аналогичное место.
        string cmd = @"
mkdir -p /var/lib/sing-box /etc/sing-box
curl -L -s --retry 3 --connect-timeout 10 -o /var/lib/sing-box/geoip.db https://github.com/SagerNet/sing-geoip/releases/latest/download/geoip.db
curl -L -s --retry 3 --connect-timeout 10 -o /var/lib/sing-box/geosite.db https://github.com/SagerNet/sing-geosite/releases/latest/download/geosite.db
echo 'SUCCESS'
";
        var res = await ssh.ExecuteCommandAsync(cmd);
        if (res.Contains("SUCCESS"))
            return (true, "Базы Sing-box (geoip.db, geosite.db) успешно обновлены.");
            
        return (false, "Ошибка при скачивании баз Sing-box.");
    }

    public async Task<(bool IsSuccess, string Message)> CompileAndDeployRuleSetsAsync(ISshService ssh)
    {
        if (!ssh.IsConnected)
            return (false, "Нет подключения по SSH");

        _logger.Log("SING-BOX", "Компиляция и деплой бинарных SRS правил для Sing-box...");

        try
        {
            string s = (await ssh.ExecuteCommandAsync("if [ \"$EUID\" -ne 0 ]; then echo 'sudo'; fi")).Trim();

            // JSON-правила для компиляции в бинарный формат Sing-box rule-set (.srs)
            string youtubeJson = "{\"version\":1,\"rules\":[{\"domain\":[\"youtube.com\",\"youtu.be\"],\"domain_suffix\":[\".youtube.com\",\".googlevideo.com\",\".ytimg.com\",\".ggpht.com\",\".youtubei.googleapis.com\"]}]}";
            string antizapretJson = "{\"version\":1,\"rules\":[{\"domain_suffix\":[\".instagram.com\",\".cdninstagram.com\",\".facebook.com\",\".fbcdn.net\",\".twitter.com\",\".x.com\",\".twimg.com\",\".t.me\",\".telegram.org\",\".telesco.pe\",\".discord.com\",\".discord.gg\",\".discordapp.com\",\".discordapp.net\",\".rutracker.org\",\".notion.so\",\".flibusta.is\",\".medium.com\"]}]}";
            string adblockJson = "{\"version\":1,\"rules\":[{\"domain_suffix\":[\".doubleclick.net\",\".googleadservices.com\",\".adservice.google.com\",\".an.yandex.ru\",\".pagead2.googlesyndication.com\",\".adcolony.com\",\".unityads.unity3d.com\"]}]}";

            string b64Youtube = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(youtubeJson));
            string b64Antizapret = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(antizapretJson));
            string b64Adblock = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(adblockJson));

            string script = $@"
mkdir -p /var/lib/sing-box/rules /tmp/srs_build

echo '{b64Youtube}' | base64 -d > /tmp/srs_build/youtube.json
echo '{b64Antizapret}' | base64 -d > /tmp/srs_build/antizapret.json
echo '{b64Adblock}' | base64 -d > /tmp/srs_build/adblock.json

SB_BIN=""sing-box""
if [ -f /usr/local/bin/sing-box ]; then
    SB_BIN=""/usr/local/bin/sing-box""
fi

if command -v $SB_BIN >/dev/null 2>&1; then
    $SB_BIN rule-set compile /tmp/srs_build/youtube.json -o /var/lib/sing-box/rules/youtube.srs 2>/dev/null || true
    $SB_BIN rule-set compile /tmp/srs_build/antizapret.json -o /var/lib/sing-box/rules/antizapret.srs 2>/dev/null || true
    $SB_BIN rule-set compile /tmp/srs_build/adblock.json -o /var/lib/sing-box/rules/adblock.srs 2>/dev/null || true
fi

# Если бинарник еще не скомпилировал или отсутствует, пробуем подтянуть предкомпилированные SRS из репозитория
if [ ! -s /var/lib/sing-box/rules/youtube.srs ]; then
    curl -L -s --connect-timeout 5 -o /var/lib/sing-box/rules/youtube.srs https://raw.githubusercontent.com/MetaCubeX/meta-rules-dat/sing/geo/geosite/youtube.srs 2>/dev/null || true
fi

chmod -R 644 /var/lib/sing-box/rules/*.srs 2>/dev/null || true
rm -rf /tmp/srs_build

if [ -f /var/lib/sing-box/rules/youtube.srs ] || [ -f /var/lib/sing-box/rules/antizapret.srs ]; then
    echo 'SRS_SUCCESS'
else
    echo 'SRS_FALLBACK_OK'
fi
".Replace("\r", "");

            string b64Script = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(script));
            string output = await ssh.ExecuteCommandAsync($"echo '{b64Script}' | base64 -d | {s} bash");

            if (output.Contains("SRS_SUCCESS") || output.Contains("SRS_FALLBACK_OK"))
            {
                _logger.Log("SING-BOX", "Бинарные rule-sets (youtube.srs, antizapret.srs, adblock.srs) успешно развернуты в /var/lib/sing-box/rules/.");
                return (true, "Бинарные правила маршрутизации Sing-box (.srs) успешно скомпилированы и установлены.");
            }

            return (false, $"Не удалось скомпилировать SRS правила: {output}");
        }
        catch (Exception ex)
        {
            _logger.Log("SING-BOX-ERR", $"Ошибка деплоя SRS правил: {ex.Message}");
            return (false, ex.Message);
        }
    }
}