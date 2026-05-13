using Wpf.Ui.Controls;

namespace KoFFPanel.Presentation.Features.Analytics;

public partial class FraudScoringWindow : FluentWindow
{
    // ИСПРАВЛЕНИЕ: Внедряем ViewModel через конструктор и привязываем её к DataContext
    public FraudScoringWindow(FraudScoringViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}