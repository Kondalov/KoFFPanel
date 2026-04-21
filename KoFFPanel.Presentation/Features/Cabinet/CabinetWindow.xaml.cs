
using Wpf.Ui.Controls;

namespace KoFFPanel.Presentation.Features.Cabinet;

public partial class CabinetWindow : FluentWindow
{
    public CabinetWindow(CabinetViewModel viewModel)
    {
        InitializeComponent();

        // Связываем наше окно с ViewModel, чтобы заработали Binding'и
        DataContext = viewModel;
    }
}