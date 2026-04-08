using KoFFPanel.Presentation.ViewModels;
using System.Windows;

namespace KoFFPanel.Presentation.Views;

public partial class AddClientWindow : Wpf.Ui.Controls.FluentWindow
{
    public AddClientWindow(AddClientViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Передаем команду закрытия во ViewModel
        viewModel.CloseAction = () => this.Close();
    }
}