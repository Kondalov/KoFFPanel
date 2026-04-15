using KoFFPanel.Application.Interfaces.ProtocolBuilders;
using System;
using System.Collections.Generic;
using System.Linq;

namespace KoFFPanel.Application.Services;

public static class CoreTypes
{
    public const string Xray = "xray";
    public const string SingBox = "sing-box";
    public const string TrustTunnel = "trusttunnel";
}

public sealed class ProtocolFactory
{
    private readonly IEnumerable<IProtocolBuilder> _builders;

    public ProtocolFactory(IEnumerable<IProtocolBuilder> builders)
    {
        _builders = builders;
    }

    // Умный фильтр: жестко распределяет протоколы по выбранному ядру
    public IEnumerable<IProtocolBuilder> GetAvailableProtocols(string coreType)
    {
        return coreType.ToLower() switch
        {
            CoreTypes.SingBox => _builders.Where(b =>
                b.ProtocolType.Equals("vless", StringComparison.OrdinalIgnoreCase) ||
                b.ProtocolType.Equals("hysteria2", StringComparison.OrdinalIgnoreCase)),

            CoreTypes.Xray => _builders.Where(b =>
                b.ProtocolType.Equals("vless", StringComparison.OrdinalIgnoreCase) && b.TransportType == "tcp"),

            CoreTypes.TrustTunnel => _builders.Where(b =>
                b.ProtocolType.Equals("trusttunnel", StringComparison.OrdinalIgnoreCase)),

            _ => Enumerable.Empty<IProtocolBuilder>()
        };
    }

    public IProtocolBuilder? GetBuilder(string protocolType) =>
        _builders.FirstOrDefault(b => b.ProtocolType.Equals(protocolType, StringComparison.OrdinalIgnoreCase));
}