using KoFFPanel.Domain.Entities;
using KoFFPanel.Presentation.Features.Cabinet; using KoFFPanel.Presentation.Features.Terminal; using KoFFPanel.Presentation.Features.Deploy; using KoFFPanel.Presentation.Features.Analytics; using KoFFPanel.Presentation.Features.Management; using KoFFPanel.Presentation.Features.Config;
using System.Collections.Generic;

namespace KoFFPanel.Presentation.Services;

public class ServerSelectionService : IServerSelectionService
{
    public VpnProfile? SelectServer(List<VpnProfile> servers, VpnProfile currentServer)
    {
        var selectionWindow = new ServerSelectionWindow(servers, currentServer);

        // Явное указание System.Windows.Application решает конфликт пространств имен
        if (System.Windows.Application.Current.MainWindow != null)
        {
            selectionWindow.Owner = System.Windows.Application.Current.MainWindow;
        }

        bool? result = selectionWindow.ShowDialog();

        if (result == true)
        {
            return selectionWindow.SelectedServer;
        }

        return null;
    }
}
