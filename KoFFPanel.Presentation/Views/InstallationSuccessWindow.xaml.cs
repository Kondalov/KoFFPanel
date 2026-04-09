using System.Windows;
using Wpf.Ui.Controls;

namespace KoFFPanel.Presentation.Views;

public partial class InstallationSuccessWindow : FluentWindow
{
    public InstallationSuccessWindow()
    {
        InitializeComponent();
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        this.Close();
    }
}