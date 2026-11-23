using System.Windows.Controls;
using System.Windows.Input;
using Songify.Core.Models;
using Wpf.Ui.Controls;

namespace Songify.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    // Tab lands on the grid itself, which shows no focus. Move focus to a cell so the row's focus border shows.
    private void TrackGrid_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!ReferenceEquals(e.NewFocus, TrackGrid) || TrackGrid.Items.Count == 0) return;
        var item = TrackGrid.SelectedItem ?? TrackGrid.Items[0];
        TrackGrid.ScrollIntoView(item);
        TrackGrid.UpdateLayout(); // realize the row if it was virtualized away
        if (TrackGrid.Columns[0].GetCellContent(item)?.Parent is DataGridCell cell) cell.Focus();
    }

    // DataGrid.SelectedItems isn't bindable; mirror it into the view model.
    private void TrackGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        ViewModel.SelectedTracks = TrackGrid.SelectedItems.OfType<TrackRecord>().ToList();
}
