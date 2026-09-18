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

                // Умная 3-позиционная реактивная сортировка в реальном времени:
                // 1. Онлайн сверху (Descending): только что подключившиеся клиенты мгновенно поднимаются вверх, отключившиеся уходят вниз
                // 2. Офлайн сверху (Ascending): отключенные клиенты сверху
                // 3. Сброс к исходному порядку (null)
                var currentDirection = e.Column.SortDirection;
                if (currentDirection == null)
                {
                    e.Column.SortDirection = ListSortDirection.Descending;
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(new SortDescription(nameof(VpnClient.IsOnline), ListSortDirection.Descending));
                    view.SortDescriptions.Add(new SortDescription(nameof(VpnClient.ConnectedAt), ListSortDirection.Descending));
                    view.SortDescriptions.Add(new SortDescription(nameof(VpnClient.ActiveConnections), ListSortDirection.Descending));
                    view.SortDescriptions.Add(new SortDescription(nameof(VpnClient.LastOnline), ListSortDirection.Descending));

                    if (view is ICollectionViewLiveShaping liveView && liveView.CanChangeLiveSorting)
                    {
                        liveView.LiveSortingProperties.Clear();
                        liveView.LiveSortingProperties.Add(nameof(VpnClient.IsOnline));
                        liveView.LiveSortingProperties.Add(nameof(VpnClient.ConnectedAt));
                        liveView.LiveSortingProperties.Add(nameof(VpnClient.ActiveConnections));
                        liveView.LiveSortingProperties.Add(nameof(VpnClient.LastOnline));
                        liveView.IsLiveSorting = true;
                    }
                }
                else if (currentDirection == ListSortDirection.Descending)
                {
                    e.Column.SortDirection = ListSortDirection.Ascending;
                    view.SortDescriptions.Clear();
                    view.SortDescriptions.Add(new SortDescription(nameof(VpnClient.IsOnline), ListSortDirection.Ascending));
                    view.SortDescriptions.Add(new SortDescription(nameof(VpnClient.ActiveConnections), ListSortDirection.Ascending));
                    view.SortDescriptions.Add(new SortDescription(nameof(VpnClient.LastOnline), ListSortDirection.Ascending));

                    if (view is ICollectionViewLiveShaping liveView && liveView.CanChangeLiveSorting)
                    {
                        liveView.LiveSortingProperties.Clear();
                        liveView.LiveSortingProperties.Add(nameof(VpnClient.IsOnline));
                        liveView.LiveSortingProperties.Add(nameof(VpnClient.ConnectedAt));
                        liveView.LiveSortingProperties.Add(nameof(VpnClient.ActiveConnections));
                        liveView.LiveSortingProperties.Add(nameof(VpnClient.LastOnline));
                        liveView.IsLiveSorting = true;
                    }
                }
                else
                {
                    e.Column.SortDirection = null;
                    view.SortDescriptions.Clear();

                    if (view is ICollectionViewLiveShaping liveView && liveView.CanChangeLiveSorting)
                    {
                        liveView.IsLiveSorting = false;
                        liveView.LiveSortingProperties.Clear();
                    }
                }
            }
        }
    }
}