using Wpf.Ui.Controls;
using System;
namespace KoFFPanel.Presentation.Views
{
    public partial class CustomConfigWindow : FluentWindow
    {
        public CustomConfigWindow(ViewModels.CustomConfigViewModel vm)
        {
            InitializeComponent();
            DataContext = vm;
            vm.CloseAction = new Action(this.Close);
        }
    }
}