using System.Windows;

namespace KoFFPanel.Presentation.Features.Shared.Dialogs;

public partial class InputTextDialog : Wpf.Ui.Controls.FluentWindow
{
    public string InputText { get; private set; } = string.Empty;

    public InputTextDialog(Window owner, string title, string message, string initialText = "")
    {
        InitializeComponent();
        this.Owner = owner;
        this.Title = title;
        MessageLabel.Text = message;
        InputTextBox.Text = initialText;
        InputTextBox.Focus();
        InputTextBox.SelectAll();
    }

    private void SaveBtn_Click(object sender, RoutedEventArgs e)
    {
        InputText = InputTextBox.Text;
        DialogResult = true;
        Close();
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
