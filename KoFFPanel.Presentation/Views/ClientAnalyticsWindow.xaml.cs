using KoFFPanel.Presentation.ViewModels;
using Wpf.Ui.Controls;

namespace KoFFPanel.Presentation.Views;

public partial class ClientAnalyticsWindow : FluentWindow
{
    public ClientAnalyticsWindow(ClientAnalyticsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}