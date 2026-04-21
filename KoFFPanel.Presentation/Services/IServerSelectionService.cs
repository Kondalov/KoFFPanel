using KoFFPanel.Domain.Entities;
using KoFFPanel.Presentation.Features.Cabinet; using KoFFPanel.Presentation.Features.Terminal; using KoFFPanel.Presentation.Features.Deploy; using KoFFPanel.Presentation.Features.Analytics; using KoFFPanel.Presentation.Features.Management; using KoFFPanel.Presentation.Features.Config;
using System.Collections.Generic;

namespace KoFFPanel.Presentation.Services;

public interface IServerSelectionService
{
    VpnProfile? SelectServer(List<VpnProfile> servers, VpnProfile currentServer);
}
