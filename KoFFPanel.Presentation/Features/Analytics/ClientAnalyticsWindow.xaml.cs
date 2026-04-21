
using Wpf.Ui.Controls;

namespace KoFFPanel.Presentation.Features.Analytics;

public partial class ClientAnalyticsWindow : FluentWindow
{
    public ClientAnalyticsWindow(ClientAnalyticsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}