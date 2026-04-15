using System.Windows;

namespace KoFFPanel.Presentation.Views.Extras;

public partial class InputTextDialog : Wpf.Ui.Controls.FluentWindow
{
    public string InputText { get; set; } = "";
    public string DialogTitle { get; set; } = "";
    public string DialogPrompt { get; set; } = "";

    public InputTextDialog(Window owner, string title, string prompt, string defaultText = "")
    {
        InitializeComponent();
        Owner = owner;
        DialogTitle = title;
        DialogPrompt = prompt;
        InputText = defaultText;
        DataContext = this;

        Loaded += (s, e) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}