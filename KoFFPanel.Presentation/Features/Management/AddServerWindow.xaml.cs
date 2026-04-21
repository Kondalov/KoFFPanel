
using System;
using Wpf.Ui.Controls;

namespace KoFFPanel.Presentation.Features.Management;

public partial class AddServerWindow : FluentWindow
{
    public AddServerWindow(AddServerViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;

        // Даем ViewModel возможность закрыть это окно
        viewModel.CloseAction = new Action(this.Close);
    }
}