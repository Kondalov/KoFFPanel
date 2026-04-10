using KoFFPanel.Presentation.ViewModels;
using System;
using Wpf.Ui.Controls;

namespace KoFFPanel.Presentation.Views;

public partial class ClientConfigWindow : FluentWindow
{
    public ClientConfigWindow(ClientConfigViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseAction = new Action(this.Close);
    }
}