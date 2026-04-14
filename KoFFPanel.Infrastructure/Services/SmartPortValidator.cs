using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

public interface ISmartPortValidator
{
    Task<(bool IsValid, string ErrorMessage)> ValidatePortAsync(ISshService ssh, string serverId, int port, string protocolType);
    Task<int> SuggestBestPortAsync(ISshService ssh, string serverId, string protocolType);
}

public class SmartPortValidator : ISmartPortValidator
{
    private readonly IProfileRepository _profileRepository;
    private readonly IAppLogger _logger; // ИСПРАВЛЕНИЕ: Инъекция логгера для сбора информации

    public SmartPortValidator(IProfileRepository profileRepository, IAppLogger logger)
    {
        _profileRepository = profileRepository;
        _logger = logger;
    }

    private string GetTransport(string protocol) => protocol.ToLower() == "vless" ? "tcp" : "udp";

    public async Task<(bool IsValid, string ErrorMessage)> ValidatePortAsync(ISshService ssh, string serverId, int port, string protocolType)
    {
        if (port < 1 || port > 65535) return (false, "Порт должен быть от 1 до 65535.");

        // Уровень 1: Системные порты (Защита от суицида)
        var criticalPorts = new HashSet<int> { 22, 53, 80 };
        if (criticalPorts.Contains(port)) return (false, $"Критическая ошибка: Порт {port} зарезервирован системой (SSH/DNS/HTTP)!");

        string targetTransport = GetTransport(protocolType);
        var profile = _profileRepository.LoadProfiles()?.FirstOrDefault(p => p.Id == serverId);

        bool isReinstall = false;

        // Уровень 2 и 3: Внутренние коллизии панели
        if (profile != null && profile.Inbounds != null)
        {
            // ИСПРАВЛЕНИЕ: Если это переустановка ТОГО ЖЕ протокола на ТОТ ЖЕ порт — мы не блокируем!
            var existingSameProtocol = profile.Inbounds.FirstOrDefault(i => i.Port == port && i.Protocol.ToLower() == protocolType.ToLower());
            if (existingSameProtocol != null)
            {
                isReinstall = true;
            }
            else
            {
                // Нельзя ставить разные протоколы с одинаковым транспортом на один порт
                foreach (var inbound in profile.Inbounds.Where(i => i.Port == port))
                {
                    if (GetTransport(inbound.Protocol) == targetTransport)
                    {
                        return (false, $"Конфликт: Порт {port} ({targetTransport.ToUpper()}) уже занят протоколом {inbound.Protocol.ToUpper()}!");
                    }
                }
            }
        }

        // Уровень 4: Физическое сканирование ОС (Live Check)
        if (ssh != null && ssh.IsConnected)
        {
            // ИСПРАВЛЕНИЕ: Добавлен параметр -p, чтобы Linux вернул название процесса (PID и имя)
            string sshCmd = targetTransport == "tcp" ? $"ss -tlnp | grep ':{port} '" : $"ss -ulnp | grep ':{port} '";
            string sysCheck = await ssh.ExecuteCommandAsync(sshCmd);

            if (!string.IsNullOrWhiteSpace(sysCheck))
            {
                // Записываем в логи, кто именно посмел занять наш порт
                _logger.Log("PORT-VALIDATOR", $"Проверка порта {port}. Ответ ОС: {sysCheck.Trim()}");

                // ИСПРАВЛЕНИЕ: Умный обход! Если порт занят НАШИМ ядром, мы пропускаем валидацию.
                if (sysCheck.Contains("sing-box") || sysCheck.Contains("xray"))
                {
                    return (true, "Занят нашим ядром (Допустимо)");
                }

                return (false, $"ОС Конфликт: Порт {port} ({targetTransport.ToUpper()}) занят сторонним процессом!");
            }
        }

        return (true, isReinstall ? "Занят текущим протоколом (Переустановка)" : "Порт свободен");
    }

    // Уровень 5: Умный Auto-Suggest
    public async Task<int> SuggestBestPortAsync(ISshService ssh, string serverId, string protocolType)
    {
        // Пытаемся всеми силами занять 443! Если занят сторонним ПО - идем по резервным Cloudflare портам
        int[] preferredPorts = { 443, 8443, 4433, 2053, 2083, 8080 };

        foreach (int port in preferredPorts)
        {
            var validation = await ValidatePortAsync(ssh, serverId, port, protocolType);
            if (validation.IsValid) return port;
        }

        // Если всё занято, ищем первый свободный в высоком диапазоне
        for (int p = 40000; p < 41000; p++)
        {
            var validation = await ValidatePortAsync(ssh, serverId, p, protocolType);
            if (validation.IsValid) return p;
        }

        return 443; // Фоллбэк
    }
}