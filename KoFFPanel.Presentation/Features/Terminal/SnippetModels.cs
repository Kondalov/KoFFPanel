using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace KoFFPanel.Presentation.Features.Terminal;

public partial class SnippetItem : ObservableObject
{
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private string _command = "";
}

public partial class SnippetSubCategory : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ObservableCollection<SnippetItem> _snippets = new();
}

public partial class SnippetCategory : ObservableObject
{
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ObservableCollection<SnippetSubCategory> _subCategories = new();
}
