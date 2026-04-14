using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using System.Collections.Generic;
using System.Linq;

namespace KoFFPanel.Application.Services;

public class ProtocolFactory
{
    private readonly IEnumerable<IProtocolBuilder> _builders;

    public ProtocolFactory(IEnumerable<IProtocolBuilder> builders)
    {
        _builders = builders;
    }

    // Умный фильтр: распределяет протоколы по ядрам
    public IEnumerable<IProtocolBuilder> GetAvailableProtocols(bool isSingBox)
    {
        if (isSingBox)
        {
            // Sing-box получает весь список (TrustTunnel дополнительно скрывается во ViewModel для безопасности)
            return _builders;
        }
        else
        {
            // ИСПРАВЛЕНИЕ: Xray умеет TCP (VLESS) + эксклюзивно поддерживает TrustTunnel (QUIC/UDP)
            return _builders.Where(b => b.TransportType == "tcp" || b.ProtocolType.ToLower() == "trusttunnel");
        }
    }

    public IProtocolBuilder? GetBuilder(string protocolType)
    {
        return _builders.FirstOrDefault(b => b.ProtocolType == protocolType);
    }
}