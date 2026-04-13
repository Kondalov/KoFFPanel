using KoFFPanel.Presentation.ViewModels;
using System.Windows;

namespace KoFFPanel.Presentation.Views;

public partial class ClientProtocolsWindow : Wpf.Ui.Controls.FluentWindow
{
    public ClientProtocolsWindow(ClientProtocolsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Передаем команду закрытия во ViewModel
        viewModel.CloseAction = () => this.Close();
    }
}