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

    // Умный фильтр: скрывает Hysteria 2 и TrustTunnel, если на сервере установлено ядро Xray!
    public IEnumerable<IProtocolBuilder> GetAvailableProtocols(bool isSingBox)
    {
        if (isSingBox)
        {
            return _builders; // Sing-box умеет всё
        }
        else
        {
            // Xray умеет только TCP (VLESS)
            return _builders.Where(b => b.TransportType == "tcp");
        }
    }

    public IProtocolBuilder? GetBuilder(string protocolType)
    {
        return _builders.FirstOrDefault(b => b.ProtocolType == protocolType);
    }
}