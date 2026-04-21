
using System;
using Wpf.Ui.Controls;

namespace KoFFPanel.Presentation.Features.Deploy;

public partial class DeployWizardWindow : FluentWindow
{
    public DeployWizardWindow(DeployWizardViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseAction = new Action(this.Close);
    }
}