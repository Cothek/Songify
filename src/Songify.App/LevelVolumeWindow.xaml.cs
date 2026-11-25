using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Wpf.Ui.Controls;
using MessageBox = System.Windows.MessageBox;
using MessageBoxButton = System.Windows.MessageBoxButton;
using MessageBoxResult = System.Windows.MessageBoxResult;

namespace Songify.App;

public partial class LevelVolumeWindow : FluentWindow
{
    private bool _closeWhenIdle;

    public LevelVolumeWindow(LevelVolumeViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Confirm = question =>
            MessageBox.Show(this, question, "Level volume", MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK;
        var handle = new WindowInteropHelper(this).EnsureHandle();
        viewModel.Recycle = path => RecycleBin.Send(path, handle);
        Loaded += async (_, _) => await viewModel.ScanAsync();

        // A file being swapped must not be abandoned halfway: stop the run, then close once it has stopped.
        Closing += (_, e) =>
        {
            if (!viewModel.IsRunning) return;
            e.Cancel = true;
            _closeWhenIdle = true;
            viewModel.StopCommand.Execute(null);
        };
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LevelVolumeViewModel.IsRunning) && !viewModel.IsRunning && _closeWhenIdle) Close();
        };
        Closed += (_, _) => viewModel.StopCommand.Execute(null); // a scan still measuring
    }

    public LevelVolumeViewModel ViewModel => (LevelVolumeViewModel)DataContext;

    /// <summary>Levelling or replacing files: an update waits for it.</summary>
    public bool IsBusy => ViewModel.IsRunning;

    // Space ticks or unticks the selected rows, like a list of checkboxes.
    private void FileGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || !ViewModel.IsIdle) return;
        var rows = FileGrid.SelectedItems.OfType<LevelFileItem>().Where(f => f.CanSelect).ToList();
        if (rows.Count == 0) return;
        e.Handled = true;
        var select = !rows.All(f => f.IsSelected);
        foreach (var row in rows) row.IsSelected = select;
    }
}
