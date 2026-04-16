using KoFFPanel.Domain.Entities;
using KoFFPanel.Presentation.Views;
using System.Collections.Generic;

namespace KoFFPanel.Presentation.Services;

public interface IServerSelectionService
{
    VpnProfile? SelectServer(List<VpnProfile> servers, VpnProfile currentServer);
}

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