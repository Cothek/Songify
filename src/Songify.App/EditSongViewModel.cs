using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Songify.Core.Audio;
using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Processing;

namespace Songify.App;

/// <summary>The "Edit song" dialog: a saved song's tags and the file name they produce.</summary>
public sealed partial class EditSongViewModel(SongifyService service, TrackRecord track) : ObservableObject
{
    /// <summary>What the file holds; null until read. Fields the dialog doesn't show (the comment) are kept from it.</summary>
    private TrackTags? _loaded;

    public string OriginalTitle => track.OriginalTitle;

    /// <summary>Why the song was flagged for checking; null if it wasn't.</summary>
    public string? ReviewReason => track.NeedsReview ? track.Note ?? "Flagged for you to check." : null;

    public bool NeedsReview => track.NeedsReview;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(FileNamePreview))] private string _songTitle = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FileNamePreview))] private string _artist = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FileNamePreview))] private string _album = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FileNamePreview))] private string _albumArtist = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FileNamePreview))] private string _year = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SaveCommand)), NotifyPropertyChangedFor(nameof(IsReading))]
    private bool _isLoaded;
    /// <summary>The file is being replaced; the dialog can't be closed until it's done.</summary>
    [ObservableProperty] private bool _isSaving;
    [ObservableProperty] private string? _titleError;
    [ObservableProperty] private string? _yearError;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsReading))] private string? _error;

    /// <summary>The file's tags are still being read.</summary>
    public bool IsReading => !IsLoaded && Error is null;

    /// <summary>Raised when Save finds an invalid field (the property name), so the window can focus it.</summary>
    public event Action<string>? InvalidField;

    /// <summary>Raised after a successful save; the window closes.</summary>
    public event Action? Saved;

    /// <summary>The file name the current fields give, from the file name pattern in the settings; null without a title.</summary>
    public string? FileNamePreview
    {
        get
        {
            if (!IsLoaded || string.IsNullOrWhiteSpace(SongTitle)) return null;
            var baseName = TrackProcessor.FileBaseName(track, BuildTags(ParseYear()), service.Settings);
            return baseName + Path.GetExtension(track.FilePath);
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            var file = await service.ReadTagsAsync(track);
            _loaded = file.Tags;
            SongTitle = file.Tags.Title;
            Artist = file.Tags.Artist ?? "";
            Album = file.Tags.Album ?? "";
            AlbumArtist = file.Tags.AlbumArtist ?? "";
            Year = file.Tags.Year?.ToString(CultureInfo.InvariantCulture) ?? "";
            IsLoaded = true;
            OnPropertyChanged(nameof(FileNamePreview));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error($"Could not read the tags of {track.FilePath}", ex);
            Error = ex.Message;
        }
    }

    partial void OnSongTitleChanged(string value) => TitleError = null;
    partial void OnYearChanged(string value) => YearError = null;

    private bool CanSave() => IsLoaded;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        Error = null;
        TitleError = string.IsNullOrWhiteSpace(SongTitle) ? "Enter a title." : null;
        var year = ParseYear();
        YearError = year is null && !string.IsNullOrWhiteSpace(Year) ? "Enter a year like 2013, or leave it empty." : null;
        if (TitleError is not null || YearError is not null)
        {
            InvalidField?.Invoke(TitleError is not null ? nameof(SongTitle) : nameof(Year));
            return;
        }

        IsSaving = true;
        try
        {
            await service.EditAsync(track, BuildTags(year));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error($"Could not edit {track.FilePath}", ex);
            Error = ex.Message;
            return;
        }
        finally
        {
            IsSaving = false;
        }
        Saved?.Invoke(); // after IsSaving is cleared: the window refuses to close while saving
    }

    private int? ParseYear() =>
        int.TryParse(Year.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var y) && ReleaseYear.IsPlausible(y) ? y : null;

    private TrackTags BuildTags(int? year) =>
        new(SongTitle.Trim(), Blank(Artist), Blank(Album), Blank(AlbumArtist), _loaded?.Comment, year);

    private static string? Blank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
