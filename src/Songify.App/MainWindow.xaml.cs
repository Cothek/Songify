using System.Windows.Automation.Peers;
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
        viewModel.EditRequested += editor => new EditSongWindow(editor) { Owner = this }.ShowDialog();
        viewModel.LevelVolumeRequested += ShowLevelVolume;
        Closing += StopLevellingFirst;
        viewModel.Update.PropertyChanged += OnUpdateChanged;
        Loaded += (_, _) =>
        {
            if (ViewModel.Update.IsVisible) AnnounceUpdate(); // e.g. "didn't start", set before this window existed
        };
    }

    // Screen readers announce the update bar's message when the stage changes (not on every percent), and keyboard
    // focus moves from the "Update" button, which disappears, to Cancel.
    private void OnUpdateChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpdateViewModel.Stage)) AnnounceUpdate();
    }

    private void AnnounceUpdate() => Dispatcher.BeginInvoke(() =>
    {
        UIElementAutomationPeer.CreatePeerForElement(UpdateMessage)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        if (ViewModel.Update.Stage == UpdateStage.Downloading) CancelUpdateButton.Focus();
    }, System.Windows.Threading.DispatcherPriority.Background);

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    /// <summary>One "Level volume" window at a time; not modal, so songs keep saving and the list stays usable.</summary>
    private void ShowLevelVolume(LevelVolumeViewModel viewModel)
    {
        if (OwnedWindows.OfType<LevelVolumeWindow>().FirstOrDefault() is { } open)
        {
            open.Activate();
            return;
        }
        new LevelVolumeWindow(viewModel) { Owner = this }.Show();
    }

    /// <summary>
    /// Closing this window exits Songify, which would cut a levelling run off between moving an original to the
    /// Recycle Bin and putting the levelled file in its place. Stop the run first, then close.
    /// </summary>
    private void StopLevellingFirst(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (OwnedWindows.OfType<LevelVolumeWindow>().FirstOrDefault(w => w.IsBusy) is not { } levelling) return;
        e.Cancel = true;
        if (!_closeAfterLevelling)
        {
            _closeAfterLevelling = true;
            levelling.ViewModel.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(LevelVolumeViewModel.IsRunning) && !levelling.ViewModel.IsRunning) Close();
            };
        }
        levelling.ViewModel.StopCommand.Execute(null);
    }

    private bool _closeAfterLevelling;

    private void TrackRow_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is DataGridRow { Item: TrackRecord track }) Edit(track);
    }

    // F2 edits, as it renames in Explorer.
    private void TrackGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F2 || TrackGrid.SelectedItem is not TrackRecord track) return;
        e.Handled = true;
        Edit(track);
    }

    private void Edit(TrackRecord track)
    {
        if (ViewModel.EditSongCommand.CanExecute(track)) ViewModel.EditSongCommand.Execute(track);
    }

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
