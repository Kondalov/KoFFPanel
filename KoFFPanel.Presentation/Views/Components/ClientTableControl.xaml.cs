using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Data;
using KoFFPanel.Domain.Entities;

namespace KoFFPanel.Presentation.Views.Components
{
    public partial class ClientTableControl : UserControl
    {
        public ClientTableControl()
        {
            InitializeComponent();
        }

        private void ClientsDataGrid_Sorting(object sender, DataGridSortingEventArgs e)
        {
            if (e.Column.SortMemberPath == nameof(VpnClient.ActiveConnections))
            {
                e.Handled = true;
                if (sender is not DataGrid dataGrid || dataGrid.ItemsSource == null) return;

                var view = CollectionViewSource.GetDefaultView(dataGrid.ItemsSource);
                if (view == null) return;

                // Умная 3-позиционная сортировка:
                // 1. Онлайн сверху (Descending)
                // 2. Офлайн сверху (Ascending)
                // 3. Сброс к исходному порядку (null)
                var currentDirection = e.Column.SortDirection;
                if (currentDirection == null)
                {
                    e.Column.SortDirection = ListSortDirection.Descending;
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(new SortDescription(nameof(VpnClient.ActiveConnections), ListSortDirection.Descending));
                }
                else if (currentDirection == ListSortDirection.Descending)
                {
                    e.Column.SortDirection = ListSortDirection.Ascending;
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(new SortDescription(nameof(VpnClient.ActiveConnections), ListSortDirection.Ascending));
                }
                else
                {
                    e.Column.SortDirection = null;
                    view.SortDescriptions.Clear();
                }
            }
        }
    }
}