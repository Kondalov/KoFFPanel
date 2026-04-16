using KoFFPanel.Domain.Entities;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace KoFFPanel.Presentation.Views;

public partial class ServerSelectionWindow : FluentWindow
{
    public VpnProfile? SelectedServer { get; private set; }

    public ServerSelectionWindow(List<VpnProfile> servers, VpnProfile currentServer)
    {
        InitializeComponent();
        ServersList.ItemsSource = servers;

        var defaultSelection = servers.FirstOrDefault(s => s.Id == currentServer?.Id);
        if (defaultSelection != null)
        {
            ServersList.SelectedItem = defaultSelection;
        }
    }

    private void ServersList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ConfirmBtn.IsEnabled = ServersList.SelectedItem != null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (ServersList.SelectedItem is VpnProfile selected)
        {
            SelectedServer = selected;
            DialogResult = true;
            Close();
        }
    }
}