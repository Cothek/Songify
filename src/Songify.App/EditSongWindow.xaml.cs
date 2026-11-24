using Wpf.Ui.Controls;

namespace Songify.App;

public partial class EditSongWindow : FluentWindow
{
    public EditSongWindow(EditSongViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Saved += () => DialogResult = true;
        viewModel.InvalidField += field =>
        {
            var input = field == nameof(EditSongViewModel.Year) ? YearInput : TitleInput;
            input.Focus();
            input.SelectAll();
        };
        // A save can't be abandoned halfway: the file is being replaced.
        Closing += (_, e) => e.Cancel = viewModel.IsSaving;
        Loaded += async (_, _) =>
        {
            try
            {
                await viewModel.LoadAsync();
            }
            catch (OperationCanceledException)
            {
                return; // Songify is closing
            }
            if (!viewModel.IsLoaded) return;
            TitleInput.Focus();
            TitleInput.SelectAll();
        };
    }
}
