using System;
using System.Windows;

namespace KoFFPanel.Presentation.Features.Config;

public partial class CustomConfigWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly CustomConfigViewModel _viewModel;

    public CustomConfigWindow(CustomConfigViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;
    }

    private void ApplyConfig_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
