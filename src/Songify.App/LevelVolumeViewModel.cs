using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Songify.Core.Audio;
using Songify.Core.Infrastructure;
using Songify.Core.Models;
using Songify.Core.Processing;

namespace Songify.App;

public enum LevelFileState { NotMeasured, Measuring, Ready, CantLevel, Working, Failed }

/// <summary>One file in the "Level volume" list.</summary>
public sealed partial class LevelFileItem(string path, string relativePath) : ObservableObject
{
    public string Path { get; } = path;
    public string RelativePath { get; } = relativePath;

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LoudnessText), nameof(MethodText), nameof(MethodHint))]
    private LevelAnalysis? _analysis;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ChangeText), nameof(NeedsChange), nameof(StatusText))]
    private double _plannedGain;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(NeedsChange), nameof(IsProblem))]
    private LevelFileState _state;
    /// <summary>Why it can't be levelled, or why the last action failed.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private string? _problem;
    /// <summary>What the last action did ("Levelled", "Replaced"); shown until the next one.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private string? _outcome;
    /// <summary>A levelled copy of it is in the copies folder.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private bool _hasCopy;
    /// <summary>The verb of the action running on it ("Levelling…").</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private string? _working;
    /// <summary>Its peaks keep it from being turned up as far as the target.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText))] private bool _heldBackByPeaks;

    /// <summary>Measured and not at the target (a failed attempt can be tried again).</summary>
    public bool NeedsChange => State is LevelFileState.Ready or LevelFileState.Failed && Analysis is not null && PlannedGain != 0;
    public bool CanSelect => State != LevelFileState.CantLevel;
    public bool IsProblem => State is LevelFileState.CantLevel or LevelFileState.Failed;

    public string LoudnessText => Analysis is { } a ? $"{a.Loudness.IntegratedLufs:0.0;−0.0} LUFS" : "";

    public string ChangeText => Analysis is null ? "" : PlannedGain == 0 ? "None" : $"{PlannedGain:+0.0;−0.0} dB";

    public string MethodText => Analysis?.Method switch
    {
        LevelMethod.Reencode => "Re-encoded",
        null => "",
        _ => "Lossless",
    };

    public string? MethodHint => Analysis?.Method switch
    {
        LevelMethod.Mp3Gain => "MP3: the volume changes in steps of 1.5 dB, without re-encoding.",
        LevelMethod.OpusHeader => "Opus: the volume is set in the file's header, without re-encoding.",
        LevelMethod.Rewrite => "FLAC/WAV: the samples are scaled and saved again in the same lossless format.",
        LevelMethod.Reencode => "AAC/Vorbis: decoded and encoded again at the same bitrate, which loses a little quality.",
        _ => null,
    };

    public string StatusText => State switch
    {
        LevelFileState.NotMeasured => "Not measured",
        LevelFileState.Measuring => "Measuring…",
        LevelFileState.Working => Working ?? "",
        LevelFileState.CantLevel or LevelFileState.Failed => Problem ?? "",
        _ => Outcome ?? (HasCopy ? "Copy saved"
            : PlannedGain == 0 ? HeldBackByPeaks ? "Can't go louder without clipping" : "Already level"
            : HeldBackByPeaks ? "Turned up only as far as its peaks allow" : ""),
    };

    partial void OnStateChanged(LevelFileState value)
    {
        if (value == LevelFileState.CantLevel) IsSelected = false;
        OnPropertyChanged(nameof(CanSelect));
    }
}

/// <summary>
/// The "Level volume" window: lists the songs in a folder with their loudness, and turns the selected ones up or
/// down to a target, into a folder of copies (to listen to first) or in place. Copies can then replace their originals,
/// which go to the Recycle Bin.
/// </summary>
public sealed partial class LevelVolumeViewModel : ObservableObject
{
    private static readonly int MeasureParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    private const int LevelParallelism = 2;

    private readonly SongifyService _service;
    private CancellationTokenSource? _work;
    private bool _loading = true;
    private bool _scanned;

    public LevelVolumeViewModel(SongifyService service)
    {
        _service = service;
        var settings = service.Settings;
        _folder = settings.LevelFolder ?? settings.OutputFolder;
        _includeSubfolders = settings.LevelIncludeSubfolders;
        _saveCopies = settings.LevelSaveCopies;
        _copiesFolder = settings.LevelCopiesFolder ?? DefaultCopiesFolder(_folder);
        _target = Targets.FirstOrDefault(t => t.Value == settings.LevelTargetLufs) ?? Targets[0];
        _loading = false;
    }

    public IReadOnlyList<Choice<double>> Targets { get; } =
    [
        new(-14, "−14 LUFS: like Spotify and YouTube"),
        new(-16, "−16 LUFS: like Apple Music"),
        new(-18, "−18 LUFS: ReplayGain's level (quieter, so fewer songs are held back by their peaks)"),
    ];

    public ObservableCollection<LevelFileItem> Files { get; } = [];

    [ObservableProperty] private string _folder;
    [ObservableProperty] private bool _includeSubfolders;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(LevelLabel))] private bool _saveCopies;
    [ObservableProperty] private string _copiesFolder;
    [ObservableProperty] private Choice<double> _target;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsBusy))] private bool _isScanning;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsBusy))] private bool _isRunning;
    /// <summary>What's happening, or what the last action did.</summary>
    [ObservableProperty] private string? _message;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNoFiles))] private string? _error;

    public bool IsBusy => IsScanning || IsRunning;
    public bool IsIdle => !IsBusy;

    public int SelectedCount => Files.Count(f => f.IsSelected);
    public int ToLevelCount => Files.Count(f => f.IsSelected && f.NeedsChange);
    public int ToReplaceCount => Files.Count(f => f.IsSelected && f.HasCopy);
    public bool HasCopies => Files.Any(f => f.HasCopy);
    /// <summary>A scan found nothing (not before the first one, and not when an error says why).</summary>
    public bool HasNoFiles => _scanned && !IsScanning && Files.Count == 0 && Error is null;
    public string SelectionText => Files.Count == 0 ? "" : $"{SelectedCount} of {Files.Count} selected";

    public string LevelLabel => ToLevelCount switch
    {
        0 => SaveCopies ? "Save levelled copies" : "Level songs",
        1 => SaveCopies ? "Save 1 levelled copy" : "Level 1 song",
        var n => SaveCopies ? $"Save {n} levelled copies" : $"Level {n} songs",
    };

    public string ReplaceLabel => ToReplaceCount == 1 ? "Replace 1 original…" : $"Replace {ToReplaceCount} originals…";

    /// <summary>Header checkbox: all, none, or some (null) of the files that can be levelled.</summary>
    public bool? AllSelected
    {
        get
        {
            var selectable = Files.Where(f => f.CanSelect).ToList();
            return selectable.All(f => f.IsSelected) ? selectable.Count > 0 : selectable.Any(f => f.IsSelected) ? null : false;
        }
        set
        {
            foreach (var file in Files.Where(f => f.CanSelect)) file.IsSelected = value ?? false;
        }
    }

    /// <summary>Asks the user to confirm (the window shows the question); true to go ahead.</summary>
    public Func<string, bool>? Confirm { get; set; }

    /// <summary>Moves a replaced original to the Recycle Bin (the window sets it, to own any prompt Windows shows).</summary>
    public Action<string> Recycle { get; set; } = path => RecycleBin.Send(path, IntPtr.Zero);

    /// <summary>
    /// Copies must go somewhere apart from the songs: in the songs' own folder (or a folder above it) a copy would
    /// land on its original, or on another song.
    /// </summary>
    private string? CopiesFolderProblem(string copiesFolder)
    {
        static string Normalize(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) + Path.DirectorySeparatorChar;
        return Normalize(Folder).StartsWith(Normalize(copiesFolder), StringComparison.OrdinalIgnoreCase)
            ? "Copies can't go in the folder you're levelling, or a folder that contains it. Choose another folder for them."
            : null;
    }

    private static string DefaultCopiesFolder(string folder)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        return Path.GetDirectoryName(trimmed) is { } parent
            ? Path.Combine(parent, Path.GetFileName(trimmed) + " (levelled)")
            : Path.Combine(trimmed, "Levelled"); // a drive root
    }

    private string CopyPath(LevelFileItem file) => Path.Combine(CopiesFolder, file.RelativePath);

    partial void OnFolderChanged(string value)
    {
        if (_loading) return;
        CopiesFolder = DefaultCopiesFolder(value);
        SaveSettings();
    }

    partial void OnIncludeSubfoldersChanged(bool value)
    {
        SaveSettings();
        if (!_loading) _ = ScanAsync();
    }
    partial void OnSaveCopiesChanged(bool value) => SaveSettings();

    partial void OnCopiesFolderChanged(string value)
    {
        if (_loading) return;
        foreach (var file in Files) file.HasCopy = File.Exists(CopyPath(file));
        SaveSettings();
        Refresh();
    }

    partial void OnTargetChanged(Choice<double> value)
    {
        if (_loading) return;
        foreach (var file in Files) Plan(file);
        SaveSettings();
        Refresh();
    }

    private void SaveSettings()
    {
        if (_loading) return;
        _service.UpdateSettings(s =>
        {
            s.LevelFolder = Folder;
            s.LevelIncludeSubfolders = IncludeSubfolders;
            s.LevelSaveCopies = SaveCopies;
            s.LevelCopiesFolder = string.Equals(CopiesFolder, DefaultCopiesFolder(Folder), StringComparison.OrdinalIgnoreCase) ? null : CopiesFolder;
            s.LevelTargetLufs = Target.Value;
        });
    }

    private void Plan(LevelFileItem file)
    {
        if (file.Analysis is not { } analysis)
        {
            file.PlannedGain = 0;
            file.HeldBackByPeaks = false;
            return;
        }
        file.PlannedGain = VolumeLeveller.PlanGain(analysis, Target.Value);
        var withoutPeakLimit = analysis with { Loudness = analysis.Loudness with { TruePeakDb = double.NegativeInfinity } };
        file.HeldBackByPeaks = VolumeLeveller.PlanGain(withoutPeakLimit, Target.Value) > file.PlannedGain;
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(ToLevelCount));
        OnPropertyChanged(nameof(ToReplaceCount));
        OnPropertyChanged(nameof(HasCopies));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(LevelLabel));
        OnPropertyChanged(nameof(ReplaceLabel));
        OnPropertyChanged(nameof(AllSelected));
        OnPropertyChanged(nameof(HasNoFiles));
        LevelCommand.NotifyCanExecuteChanged();
        ReplaceOriginalsCommand.NotifyCanExecuteChanged();
        ScanCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsScanningChanged(bool value) => Refresh();
    partial void OnIsRunningChanged(bool value) => Refresh();

    private void OnFileChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LevelFileItem.IsSelected) or nameof(LevelFileItem.HasCopy) or nameof(LevelFileItem.PlannedGain)
            or nameof(LevelFileItem.State))
            Refresh();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder of songs to level",
            InitialDirectory = Directory.Exists(Folder) ? Folder : null,
        };
        if (dialog.ShowDialog() != true) return;
        Folder = dialog.FolderName;
        _ = ScanAsync();
    }

    [RelayCommand]
    private void BrowseCopiesFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose where the levelled copies go",
            InitialDirectory = Directory.Exists(CopiesFolder) ? CopiesFolder : null,
        };
        if (dialog.ShowDialog() != true) return;
        Error = CopiesFolderProblem(dialog.FolderName);
        if (Error is null) CopiesFolder = dialog.FolderName;
    }

    [RelayCommand]
    private void OpenCopiesFolder()
    {
        if (Directory.Exists(CopiesFolder)) Shell.Open(CopiesFolder);
        else Error = "There are no levelled copies yet.";
    }

    [RelayCommand]
    private void Stop() => _work?.Cancel();

    /// <summary>Lists the folder's songs and measures how loud each one is.</summary>
    [RelayCommand(CanExecute = nameof(IsIdle))]
    public async Task ScanAsync()
    {
        if (IsBusy) return;
        _scanned = true;
        Error = null;
        foreach (var file in Files) file.PropertyChanged -= OnFileChanged;
        Files.Clear();
        VolumeLeveller leveller;
        try
        {
            leveller = _service.GetLeveller();
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            Refresh();
            return;
        }
        if (!Directory.Exists(Folder))
        {
            Error = "That folder doesn't exist. Choose another one.";
            Refresh();
            return;
        }

        using var cts = _work = new CancellationTokenSource();
        IsScanning = true;
        try
        {
            Message = "Looking for songs…";
            var (folder, copies, recurse) = (Folder, CopiesFolder, IncludeSubfolders);
            foreach (var path in await Task.Run(() => FindSongs(folder, copies, recurse), cts.Token))
            {
                var file = new LevelFileItem(path, Path.GetRelativePath(folder, path));
                file.HasCopy = File.Exists(CopyPath(file));
                file.PropertyChanged += OnFileChanged;
                Files.Add(file);
            }
            Refresh();

            var measured = 0;
            await ForEachAsync(Files.ToList(), MeasureParallelism, async file =>
            {
                await MeasureAsync(leveller, file, cts.Token);
                Message = $"Measuring loudness… {++measured} of {Files.Count}";
            }, cts.Token);
            Message = Files.Count == 0 ? null : Summary();
        }
        catch (OperationCanceledException)
        {
            Message = "Stopped.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Couldn't read the folder: {ex.Message}";
        }
        finally
        {
            _work = null;
            IsScanning = false;
        }
    }

    /// <summary>Songs Songify can level in <paramref name="folder"/>, without the copies folder or files mid-swap.</summary>
    private static List<string> FindSongs(string folder, string copiesFolder, bool recurse)
    {
        var copies = Path.TrimEndingDirectorySeparator(Path.GetFullPath(copiesFolder)) + Path.DirectorySeparatorChar;
        var options = new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true };
        return Directory.EnumerateFiles(folder, "*", options)
            .Where(p => VolumeLeveller.Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase))
            .Where(p => !Path.GetFileName(p).StartsWith("~songify-", StringComparison.OrdinalIgnoreCase))
            .Where(p => !Path.GetFullPath(p).StartsWith(copies, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task MeasureAsync(VolumeLeveller leveller, LevelFileItem file, CancellationToken ct)
    {
        file.State = LevelFileState.Measuring;
        try
        {
            file.Analysis = await leveller.AnalyzeAsync(file.Path, ct);
            Plan(file);
            file.State = LevelFileState.Ready;
        }
        catch (InvalidOperationException ex)
        {
            file.Problem = ex.Message;
            file.State = LevelFileState.CantLevel;
        }
        catch (OperationCanceledException)
        {
            file.State = LevelFileState.NotMeasured;
            throw;
        }
        catch (Exception ex) // e.g. ffmpeg missing or blocked: say so on the file rather than crash the window
        {
            AppLog.Error($"Couldn't measure {file.RelativePath}", ex);
            file.Problem = $"Couldn't measure it: {ex.Message}";
            file.State = LevelFileState.CantLevel;
        }
    }

    /// <summary>"40 songs. 12 need levelling; 3 can't be levelled."</summary>
    private string Summary()
    {
        var needs = Files.Count(f => f.NeedsChange);
        var cant = Files.Count(f => f.State == LevelFileState.CantLevel);
        var text = $"{Songs(Files.Count)}. {(needs == 0 ? "All are already level" : $"{needs} need levelling")}";
        return cant == 0 ? text + "." : text + $"; {cant} can't be levelled.";
    }

    private static string Songs(int n) => n == 1 ? "1 song" : $"{n} songs";

    private bool CanLevel() => IsIdle && ToLevelCount > 0;

    /// <summary>Levels the selected songs: into copies, or in place (the originals go to the Recycle Bin).</summary>
    [RelayCommand(CanExecute = nameof(CanLevel))]
    private async Task LevelAsync()
    {
        var files = Files.Where(f => f.IsSelected && f.NeedsChange).ToList();
        if (files.Count == 0 || IsBusy) return;
        if (SaveCopies && CopiesFolderProblem(CopiesFolder) is { } problem)
        {
            Error = problem;
            return;
        }
        if (!SaveCopies && Confirm?.Invoke(
                $"Change the volume of {Songs(files.Count)} in place? The originals go to the Recycle Bin.") != true)
            return;
        await RunAsync(files, SaveCopies ? "Saving levelled copies" : "Levelling", async (leveller, file, ct) =>
        {
            var copies = SaveCopies;
            await leveller.LevelAsync(file.Path, copies ? CopyPath(file) : null, file.Analysis!, file.PlannedGain, Recycle, ct);
            if (copies)
            {
                file.HasCopy = true;
                return "Copy saved";
            }
            // Not cancellable: the file has changed, so its old measurement must not stay (levelling it again
            // would apply the change twice).
            await MeasureAsync(leveller, file, CancellationToken.None);
            return "Levelled";
        }, done => SaveCopies
            ? $"Saved {done} levelled {(done == 1 ? "copy" : "copies")}. Listen to them, then replace the originals with them."
            : $"Levelled {Songs(done)}. The originals are in the Recycle Bin.");
    }

    private bool CanReplace() => IsIdle && ToReplaceCount > 0;

    /// <summary>Puts the selected songs' levelled copies in place of the originals, which go to the Recycle Bin.</summary>
    [RelayCommand(CanExecute = nameof(CanReplace))]
    private async Task ReplaceOriginalsAsync()
    {
        var files = Files.Where(f => f.IsSelected && f.HasCopy).ToList();
        if (files.Count == 0 || IsBusy) return;
        if (CopiesFolderProblem(CopiesFolder) is { } problem)
        {
            Error = problem;
            return;
        }
        if (Confirm?.Invoke($"Replace {Songs(files.Count)} with their levelled copies? The originals go to the Recycle Bin.") != true)
            return;
        await RunAsync(files, "Replacing", async (leveller, file, ct) =>
        {
            await VolumeLeveller.ReplaceWithCopyAsync(file.Path, CopyPath(file), Recycle);
            file.HasCopy = false;
            await MeasureAsync(leveller, file, CancellationToken.None); // as after levelling in place
            return "Replaced";
        }, done => $"Replaced {Songs(done)}. The originals are in the Recycle Bin.");
    }

    /// <summary>Runs <paramref name="action"/> on each file, a few at a time, showing progress; returns what it did.</summary>
    private async Task RunAsync(List<LevelFileItem> files, string verb,
        Func<VolumeLeveller, LevelFileItem, CancellationToken, Task<string>> action, Func<int, string> summary)
    {
        Error = null;
        VolumeLeveller leveller;
        try
        {
            leveller = _service.GetLeveller();
        }
        catch (InvalidOperationException ex)
        {
            Error = ex.Message;
            return;
        }

        using var cts = _work = new CancellationTokenSource();
        IsRunning = true;
        int done = 0, failed = 0;
        try
        {
            foreach (var file in files)
            {
                file.Outcome = null;
                file.Working = "Waiting…";
                file.State = LevelFileState.Working;
            }
            Message = $"{verb}… 0 of {files.Count}";
            await ForEachAsync(files, LevelParallelism, async file =>
            {
                file.Working = verb + "…";
                try
                {
                    file.Outcome = await action(leveller, file, cts.Token);
                    if (file.State == LevelFileState.Working) file.State = LevelFileState.Ready;
                    done++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
                {
                    AppLog.Warn($"{verb} {file.RelativePath} failed: {ex.Message}");
                    file.Problem = ex.Message;
                    file.State = LevelFileState.Failed;
                    failed++;
                }
                Message = $"{verb}… {done + failed} of {files.Count}";
            }, cts.Token);
            Message = summary(done) + (failed == 0 ? "" : $" {failed} failed; see the Status column.");
        }
        catch (OperationCanceledException)
        {
            Message = $"Stopped. {summary(done)}";
        }
        finally
        {
            foreach (var file in files.Where(f => f.State == LevelFileState.Working)) file.State = LevelFileState.Ready;
            foreach (var file in files) file.Working = null;
            _work = null;
            IsRunning = false;
        }
    }

    /// <summary>
    /// Runs <paramref name="body"/> for each item, at most <paramref name="parallelism"/> at once. Called on the UI
    /// thread, the bodies' continuations stay on it (only the ffmpeg runs and file copies happen elsewhere).
    /// </summary>
    private static async Task ForEachAsync<T>(IEnumerable<T> items, int parallelism, Func<T, Task> body, CancellationToken ct)
    {
        using var gate = new SemaphoreSlim(parallelism);
        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                await body(item);
            }
            finally
            {
                gate.Release();
            }
        }));
    }
}
