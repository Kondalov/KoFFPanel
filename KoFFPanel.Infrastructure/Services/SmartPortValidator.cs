using KoFFPanel.Application.Interfaces;
using KoFFPanel.Domain.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace KoFFPanel.Infrastructure.Services;

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

        var criticalPorts = new HashSet<int> { 22, 53, 80 };
        if (criticalPorts.Contains(port)) return (false, $"Критическая ошибка: Порт {port} зарезервирован системой (SSH/DNS/HTTP)!");

        string targetTransport = GetTransport(protocolType);
        var profile = _profileRepository.LoadProfiles()?.FirstOrDefault(p => p.Id == serverId);

        bool isReinstall = false;

        if (profile != null && profile.Inbounds != null)
        {
            var existingSameProtocol = profile.Inbounds.FirstOrDefault(i => i.Port == port && i.Protocol.ToLower() == protocolType.ToLower());
            if (existingSameProtocol != null)
            {
                isReinstall = true;
            }
            else
            {
                var conflictingInbound = profile.Inbounds.FirstOrDefault(i => i.Port == port && GetTransport(i.Protocol) == targetTransport);
                if (conflictingInbound != null)
                {
                    // ИСПРАВЛЕНИЕ: Умный алгоритм! Разрешаем замену протокола (true) и выдаем предупреждение!
                    return (true, $"ВНИМАНИЕ: Заменит {conflictingInbound.Protocol.ToUpper()} ({targetTransport.ToUpper()})!");
                }
            }
        }

        if (ssh != null && ssh.IsConnected)
        {
            string sshCmd = targetTransport == "tcp" ? $"ss -tlnp | grep ':{port} '" : $"ss -ulnp | grep ':{port} '";
            string sysCheck = await ssh.ExecuteCommandAsync(sshCmd);

            if (!string.IsNullOrWhiteSpace(sysCheck))
            {
                _logger.Log("PORT-VALIDATOR", $"Проверка порта {port}. Ответ ОС: {sysCheck.Trim()}");

                if (sysCheck.Contains("sing-box") || sysCheck.Contains("xray") || sysCheck.Contains("trusttunnel"))
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
        // Пытаемся всеми силами занять 443 или 2443 для TrustTunnel!
        int[] preferredPorts = protocolType.ToLower() == "trusttunnel" 
            ? new[] { 2443, 443, 8443, 4433 } 
            : new[] { 443, 8443, 4433, 2053, 2083, 8080 };

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