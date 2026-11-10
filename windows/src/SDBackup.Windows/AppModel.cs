using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using System.Windows.Threading;
using SDBackup.Core;

namespace SDBackup.Windows;

/// <summary>Questions and messages the model needs from the UI.</summary>
public interface IDialogs
{
    bool Confirm(string title, string message, string confirmText);
    void Alert(string title, string message);
    string? PickFolder(string title, string? initialFolder);
}

/// <summary>A confirmed import waiting to copy or copying.</summary>
public sealed record ImportJob(VolumeInfo Volume, ImportPlan Plan, IReadOnlyList<ProjectInfo> Projects, string? PrimaryFolder)
{
    public string Title => Projects.Count switch
    {
        0 => Volume.Name,
        1 => Projects[0].Name,
        _ => $"{Projects[0].Name} + {Projects.Count - 1} more",
    };
}

/// <summary>
/// The app's state and the import flow (PORTING.md §4):
/// 1. A card is inserted → it is read in the background (shoot days, cameras, what's new).
/// 2. The user picks a project for each shoot day in the panel.
/// 3. Files are copied into the project folders, verified, and recorded so they aren't offered again.
/// </summary>
public sealed class AppModel : ObservableObject, IDisposable
{
    private const int HistoryLimit = 25;

    private readonly Dispatcher _dispatcher;
    private readonly IDialogs _dialogs;
    private readonly Action<string, string> _notify;
    private readonly string? _historyFile;
    private DriveWatcher? _watcher;

    private List<VolumeInfo> _connected = [];
    private readonly List<VolumeInfo> _analysisQueue = [];
    private VolumeInfo? _analyzing;
    private CancellationTokenSource? _analysisCancel;
    private readonly List<ImportDraftViewModel> _drafts = [];
    private readonly List<ImportJob> _importQueue = [];
    private ImportJob? _activeJob;
    private CancellationTokenSource? _importCancel;
    private string? _importStopReason;
    private CancellationTokenSource? _migrationCancel;
    private string? _migrationStopReason;
    private StatusViewModel? _migrationStatus;
    private readonly List<HistoryEntry> _history;
    private int _selectedYear;
    private bool _yearsFromVideos;

    public AppModel(AppSettings settings, LibraryStore library, BackupLog log, IDialogs dialogs, Action<string, string> notify, string? historyFile)
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = settings;
        Library = library;
        Log = log;
        _dialogs = dialogs;
        _notify = notify;
        _historyFile = historyFile;
        _history = (historyFile is null ? null : Json.ReadFile<List<HistoryEntry>>(historyFile)) ?? [];

        CancelAnalysisCommand = new RelayCommand(() => _analysisCancel?.Cancel());
        CancelImportCommand = new RelayCommand(CancelImport);
        CancelMigrationCommand = new RelayCommand(CancelMigration);
        OpenProjectsFolderCommand = new RelayCommand(OpenProjectsFolder);
        OpenSettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        OpenLogCommand = new RelayCommand(OpenLog);
        QuitCommand = new RelayCommand(Quit);
        ChangeProjectsFolderCommand = new RelayCommand(ChangeProjectsFolder);
        ChangeVideosFolderCommand = new RelayCommand(ChangeVideosFolder);
        KeepVideosWithPhotosCommand = new RelayCommand(() => Settings.VideosFolder = "");
        MoveYearCommand = new RelayCommand(() => MoveYear(SelectedYear), () => CanMoveYear);
        ForgetImportedFilesCommand = new RelayCommand(ForgetImportedFiles);
        ForgetCameraNamesCommand = new RelayCommand(() => Library.ForgetCameraNames());
        ClearHistoryCommand = new RelayCommand(ClearHistory);

        Settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(AppSettings.AutoImport)) UpdateIdleStatus();
            if (e.PropertyName is nameof(AppSettings.Detection) or nameof(AppSettings.ProjectsFolder)) RefreshCards();
            if (e.PropertyName is nameof(AppSettings.ProjectsFolder) or nameof(AppSettings.VideosFolder))
            {
                OnPropertyChanged(nameof(ProjectsFolderName));
                RefreshYears();
            }
        };
        RefreshRecent();
        Library.Shoots.Changed += () => _dispatcher.Invoke(RefreshShoots);
        RefreshShoots();
    }

    // ------------------------------------------------------------------ Bindable state

    public AppSettings Settings { get; }
    public LibraryStore Library { get; }
    public BackupLog Log { get; }

    public ObservableCollection<CardViewModel> Cards { get; } = [];
    public ObservableCollection<HistoryItemViewModel> Recent { get; } = [];
    public ObservableCollection<StatusViewModel> Tasks { get; } = [];
    public ObservableCollection<int> Years { get; } = [];

    /// <summary>The first card waiting for a project.</summary>
    public ImportDraftViewModel? CurrentDraft => _drafts.FirstOrDefault();
    public int WaitingCount => _drafts.Count;
    public string MoreWaitingText => _drafts.Count > 1 ? $"+{_drafts.Count - 1} waiting" : "";

    public bool ShowIdle => Tasks.Count == 0 && _drafts.Count == 0;
    public string IdleTitle => Settings.AutoImport ? "Ready" : "Auto Import is off";
    public string IdleSubtitle => Settings.AutoImport ? "Insert an SD card to import it into a project." : "Click Import next to a card to import it.";
    public bool HasCards => Cards.Count > 0;
    public bool HasRecent => Recent.Count > 0;
    public bool IsBusy => _activeJob is not null || _analyzing is not null || _migrationStatus is not null;
    public bool IsMigrating => _migrationStatus is not null;
    public StatusViewModel? MigrationStatus => _migrationStatus;

    /// <summary>"Notch 1.2.0" (Settings). CI sets the version from the release tag; other builds are "0.0.0-dev".</summary>
    public string VersionText => s_versionText;

    private static readonly string s_versionText = MakeVersionText();

    private static string MakeVersionText()
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
        version = version.Split('+')[0]; // Without the commit .NET adds.
        return version.Length == 0 || version.StartsWith("0.0.0", StringComparison.Ordinal) ? "Notch (test build)" : $"Notch {version}";
    }

    public string ProjectsFolderName => Path.GetFileName(PathUtil.Normalize(Settings.ProjectsFolder)) is { Length: > 0 } name ? name : Settings.ProjectsFolder;

    public int SelectedYear
    {
        get => _selectedYear;
        set
        {
            if (!SetProperty(ref _selectedYear, value)) return;
            OnPropertyChanged(nameof(CanMoveYear));
            ((RelayCommand)MoveYearCommand).RaiseCanExecuteChanged();
        }
    }

    /// <summary>"Photos: 2026 › Photo › …" and "Videos: …": where a new project's photo and video go (Settings › Projects folder).</summary>
    public string FolderStructureSummary
    {
        get
        {
            var photo = FolderStructureFor(FolderStructure.Branch.Photo);
            var video = FolderStructureFor(FolderStructure.Branch.Video);
            if (HasSeparateFolders)
            {
                photo = photo.WithoutMediaType;
                video = video.WithoutMediaType;
            }
            var lines = new List<string>
            {
                "Photos: " + string.Join(" › ", photo.ExamplePath(FolderStructure.Branch.Photo)),
                "Videos: " + string.Join(" › ", video.ExamplePath(FolderStructure.Branch.Video)),
            };
            if (Settings.SeparateVideosFolder is { } videos)
            {
                lines.Add($"Videos are saved in {videos}. Photo and Video folders are left out because photos and videos go to different folders.");
            }
            return string.Join(Environment.NewLine, lines);
        }
    }

    /// <summary>The folder holding new projects' photos or videos by default.</summary>
    public string DefaultRoot(FolderStructure.Branch branch) =>
        branch == FolderStructure.Branch.Photo ? Settings.ProjectsFolder : Settings.SeparateVideosFolder ?? Settings.ProjectsFolder;

    /// <summary>Photos and videos go to different folders, so the Photo / Video level is left out.</summary>
    public bool HasSeparateFolders => Settings.HasSeparateVideosFolder;

    private FolderStructure FolderStructureFor(FolderStructure.Branch branch) => Library.Structure(branch, DefaultRoot(branch));

    public bool CanMoveYear => !IsMigrating && _activeJob is null && Years.Contains(SelectedYear);
    public bool HasYears => Years.Count > 0;

    public bool StartWithWindows
    {
        get => StartupRegistration.IsEnabled;
        set
        {
            try { StartupRegistration.SetEnabled(value); }
            catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                Log.Write($"Couldn’t change Start with Windows: {error.Message}");
            }
            OnPropertyChanged();
        }
    }

    public ICommand CancelAnalysisCommand { get; }
    public ICommand CancelImportCommand { get; }
    public ICommand CancelMigrationCommand { get; }
    public ICommand OpenProjectsFolderCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand OpenLogCommand { get; }
    public ICommand QuitCommand { get; }
    public ICommand ChangeProjectsFolderCommand { get; }
    public ICommand ChangeVideosFolderCommand { get; }
    public ICommand KeepVideosWithPhotosCommand { get; }
    public ICommand MoveYearCommand { get; }
    public ICommand ForgetImportedFilesCommand { get; }
    public ICommand ForgetCameraNamesCommand { get; }
    public ICommand ClearHistoryCommand { get; }

    public event Action? SettingsRequested;
    /// <summary>True while a folder picker opened from the panel is up, so the panel stays open behind it.</summary>
    public event Action<bool>? PanelDialogOpen;
    public event Action? QuitRequested;
    /// <summary>Raised when the number of cards waiting for a project changes (for the tray icon).</summary>
    public event Action<int>? WaitingCountChanged;

    // ------------------------------------------------------------------ Starting

    /// <summary>Starts watching drives and reads cards that are already connected.</summary>
    public void Start()
    {
        Library.Library(Settings.ProjectsFolder);
        _watcher = new DriveWatcher();
        _watcher.Changed += (arrived, removed) =>
        {
            foreach (var root in removed) DriveRemoved(root);
            Library.RefreshLocations(); // A drive with moved projects may have been connected.
            RefreshCards();
            if (Settings.AutoImport)
            {
                foreach (var root in arrived)
                {
                    if (_connected.FirstOrDefault(v => string.Equals(v.Root, root, StringComparison.OrdinalIgnoreCase)) is { } volume)
                    {
                        PrepareImport(volume);
                    }
                }
            }
        };
        RefreshCards();
        RefreshYears();
        if (Settings.AutoImport)
        {
            foreach (var volume in _connected.ToList()) PrepareImport(volume);
        }
    }

    // ------------------------------------------------------------------ Cards

    private bool IsEligible(VolumeInfo volume)
    {
        if (!volume.IsRemovableMedia) return false;
        if (PathUtil.IsInside(Settings.ProjectsFolder, volume.Root)) return false; // The projects folder is on this drive.
        return Settings.Detection == CardDetection.AnyRemovable || volume.LooksLikeMemoryCard;
    }

    public CardStatus StatusOf(VolumeInfo volume)
    {
        if (_activeJob?.Volume.Id == volume.Id) return CardStatus.Importing;
        if (_importQueue.Any(j => j.Volume.Id == volume.Id)) return CardStatus.Queued;
        if (_drafts.Any(d => d.Volume.Id == volume.Id)) return CardStatus.NeedsProject;
        if (_analyzing?.Id == volume.Id || _analysisQueue.Any(v => v.Id == volume.Id)) return CardStatus.Reading;
        return CardStatus.Idle;
    }

    /// <summary>Re-reads connected drives and updates the card list.</summary>
    public void RefreshCards()
    {
        _connected = VolumeInspector.MountedVolumes().Where(IsEligible).ToList();
        SyncCards();
    }

    private void SyncCards()
    {
        foreach (var card in Cards.ToList())
        {
            if (_connected.All(v => v.Id != card.Volume.Id)) Cards.Remove(card);
        }
        foreach (var volume in _connected)
        {
            var card = Cards.FirstOrDefault(c => c.Volume.Id == volume.Id);
            if (card is null)
            {
                var v = volume;
                card = new CardViewModel(volume,
                    new RelayCommand(() => PrepareImport(Cards.FirstOrDefault(c => c.Volume.Id == v.Id)?.Volume ?? v)),
                    new RelayCommand(() => Eject(Cards.FirstOrDefault(c => c.Volume.Id == v.Id)?.Volume ?? v, announce: false)));
                Cards.Add(card);
            }
            else
            {
                card.Update(volume);
            }
            card.Status = StatusOf(volume);
            card.Detail = LastImport(volume) is { } last
                ? $"Imported {Relative.Format(last.Date)}"
                : $"{(volume.Capacity is long c ? Format.Bytes(c) + " · " : "")}Not imported yet";
        }
        OnPropertyChanged(nameof(HasCards));
    }

    private void DriveRemoved(string root)
    {
        bool Matches(VolumeInfo v) => string.Equals(v.Root, root, StringComparison.OrdinalIgnoreCase);
        _analysisQueue.RemoveAll(Matches);
        _importQueue.RemoveAll(j => Matches(j.Volume));
        _drafts.RemoveAll(d => Matches(d.Volume));
        if (_analyzing is { } analyzing && Matches(analyzing)) _analysisCancel?.Cancel();
        if (_activeJob is { } job && Matches(job.Volume))
        {
            _importStopReason ??= "The card was removed before the import finished. Insert it again to finish.";
            _importCancel?.Cancel();
        }
        DraftsChanged();
    }

    // ------------------------------------------------------------------ Step 1: read the card

    /// <summary>Reads the card, then asks which project its files belong to.</summary>
    public void PrepareImport(VolumeInfo volume)
    {
        if (StatusOf(volume) != CardStatus.Idle) return;
        _analysisQueue.Add(volume);
        SyncCards();
        StartNextAnalysis();
    }

    private void StartNextAnalysis()
    {
        if (_analyzing is not null) return;
        while (_analysisQueue.Count > 0)
        {
            var volume = _analysisQueue[0];
            _analysisQueue.RemoveAt(0);
            if (Directory.Exists(volume.Root))
            {
                Analyze(volume);
                return;
            }
        }
    }

    private async void Analyze(VolumeInfo volume)
    {
        _analyzing = volume;
        var status = new StatusViewModel(TaskKind.Reading, "Reading card", volume.DisplayName, CancelAnalysisCommand)
        {
            Line1Left = "Looking for photos and videos…",
        };
        AddTask(status);
        var ledger = Library.Ledger(volume.CardId);
        var cancellation = new CancellationTokenSource();
        _analysisCancel = cancellation;
        SyncCards();

        CardAnalysis? analysis = null;
        Exception? failure = null;
        try
        {
            analysis = await Task.Run(() => new CardAnalyzer().Analyze(volume.Root, ledger.Contains, (done, total) =>
            {
                if (done % 20 != 0 && done != total) return;
                _dispatcher.InvokeAsync(() =>
                {
                    status.IsIndeterminate = false;
                    status.Progress = total == 0 ? 100 : 100.0 * done / total;
                    status.Line1Left = $"Finding shoot dates and cameras · {done:N0} of {Format.Count(total, "file")}";
                });
            }, cancellation.Token));
        }
        catch (Exception error)
        {
            failure = error;
        }

        _analyzing = null;
        _analysisCancel = null;
        RemoveTask(status);
        AnalysisFinished(volume, analysis, failure);
        StartNextAnalysis();
        SyncCards();
    }

    private void AnalysisFinished(VolumeInfo volume, CardAnalysis? analysis, Exception? failure)
    {
        if (failure is OperationCanceledException) return;
        if (failure is not null || analysis is null)
        {
            var message = failure?.Message ?? "Unknown error.";
            Log.Write($"Couldn’t read “{volume.DisplayName}”: {message}");
            Record(Outcome.Failed, volume.Name, volume, null, null, message);
            Notify($"Couldn’t read “{volume.Name}”", message);
            return;
        }
        if (analysis.IsEmpty)
        {
            var detail = analysis.AlreadyImported > 0
                ? $"All {Format.Count(analysis.AlreadyImported, "file")} on it were imported before."
                : "It has no photos, videos or audio.";
            Log.Write($"“{volume.DisplayName}”: nothing new. {detail}");
            Notify($"Nothing new on “{volume.Name}”", detail);
            if (Settings.EjectWhenDone && analysis.AlreadyImported > 0) Eject(volume, announce: true);
            return;
        }

        Library.RefreshLocations();
        var names = new Dictionary<string, string>();
        foreach (var model in analysis.CameraModels) names[model] = Library.CameraName(model, analysis.MakeFor(model));
        if (analysis.HasUnknownCamera)
        {
            names[""] = Library.CameraNameForCard(volume.CardId)
                ?? (analysis.CameraModels.Count == 1 ? names[analysis.CameraModels[0]] : null)
                ?? "Camera";
        }
        var draft = new ImportDraftViewModel(volume, analysis, Library.RecentProjects(), Library.Shoots.Open, names, StartImport, Dismiss, DraftServices);
        _drafts.Add(draft);
        DraftsChanged();
        Log.Write($"Read “{volume.DisplayName}”: {analysis.Items.Count} new files on {draft.Days.Count} shoot day(s), {analysis.AlreadyImported} imported before");
        Notify($"“{volume.Name}” is ready to import",
            $"{Format.Count(draft.Days.Count, "shoot day")}, {Format.Count(analysis.Items.Count, "new file")}. Click to choose the project.");
    }

    // ------------------------------------------------------------------ Step 2: the user picks projects

    private void Dismiss(ImportDraftViewModel draft)
    {
        _drafts.Remove(draft);
        DraftsChanged();
    }

    private void StartImport(ImportDraftViewModel draft)
    {
        if (!draft.IsReady) return;
        Library.RefreshLocations();
        var projects = draft.ResolveProjects(Library, DefaultRoot(FolderStructure.Branch.Photo), DefaultRoot(FolderStructure.Branch.Video));
        if (projects is null)
        {
            var folders = Settings.SeparateVideosFolder is { } videos ? $"{Settings.ProjectsFolder} or {videos}" : Settings.ProjectsFolder;
            _dialogs.Alert("The projects folder isn’t available",
                $"{folders} can’t be used. If it’s on an external drive, connect it, or choose another folder in Settings.");
            return;
        }
        // Each file needs its folder's drive: the photos' or the videos'.
        var offline = draft.Analysis.Items
            .Select(item => projects.TryGetValue(item.Day, out var project)
                && (item.Kind == MediaKind.Photo ? project.RootPath : project.VideoRoot).Length == 0 ? project : null)
            .FirstOrDefault(project => project is not null);
        if (offline is not null)
        {
            _dialogs.Alert("Connect the drive", $"“{offline.Name}” is on a drive that isn’t connected. Connect it and click Import again.");
            return;
        }

        var plan = ProjectLayout.Plan(draft.Analysis.Items, projects, draft.CameraNameFor);
        Library.RememberCameraNames(draft.CameraNames, draft.Volume.CardId);
        var (started, continued) = draft.ShootsUsed;
        foreach (var name in started) Library.Shoots.Start(name);
        foreach (var name in continued) Library.Shoots.MarkUsed(name);
        _drafts.Remove(draft);
        DraftsChanged();
        if (plan.Items.Count == 0) return;

        var used = projects.Values.Distinct().OrderBy(p => p.StartDay).ToList();
        string? primaryFolder = null;
        if (used.Count > 0)
        {
            var first = used[0];
            var hasPhotos = draft.Analysis.Items.Any(i => i.Kind == MediaKind.Photo && projects.TryGetValue(i.Day, out var p) && p.Id == first.Id);
            primaryFolder = hasPhotos ? ProjectLayout.PhotoFolder(first) : ProjectLayout.VideoFolder(first);
        }
        _importQueue.Add(new ImportJob(draft.Volume, plan, used, primaryFolder));
        StartNextImport();
    }

    // ------------------------------------------------------------------ Step 3: copy

    private void CancelImport()
    {
        _importStopReason = "Cancelled. Files copied so far were kept.";
        _importCancel?.Cancel();
    }

    private void StartNextImport()
    {
        // Imports wait while a year is being moved, in case they belong to that year.
        while (_activeJob is null && _migrationStatus is null && _importQueue.Count > 0)
        {
            var job = _importQueue[0];
            _importQueue.RemoveAt(0);
            if (Directory.Exists(job.Volume.Root))
            {
                RunImport(job);
            }
            else
            {
                Notify($"Couldn’t import “{job.Volume.Name}”", "The card was removed. Insert it again to import.");
            }
        }
        SyncCards();
    }

    private async void RunImport(ImportJob job)
    {
        _activeJob = job;
        _importStopReason = null;
        var status = new StatusViewModel(TaskKind.Importing, "Importing", $"{job.Volume.Name} → {job.Title}", CancelImportCommand);
        AddTask(status, first: true);
        var options = new BackupOptions(VerifyCopies: Settings.VerifyCopies);
        // Projects moved to another drive are imported there; if it isn't connected, the copy stops with "connect the drive".
        var roots = job.Projects.Select(p => p.RootPath).Distinct(PathUtil.Comparer).ToList();
        var destination = roots.Count == 1 ? roots[0] : Settings.ProjectsFolder;
        var cancellation = new CancellationTokenSource();
        _importCancel = cancellation;
        UpdateCanMove();
        SyncCards();
        Log.Write($"Importing “{job.Volume.DisplayName}” into {job.Title}: {Format.Count(job.Plan.FileCount, "file")}, {Format.Bytes(job.Plan.ByteCount)}");

        BackupResult? result = null;
        Exception? failure = null;
        try
        {
            result = await Task.Run(() =>
            {
                using var awake = KeepAwake.Begin();
                return new BackupEngine(options).Copy(job.Plan.Items, job.Volume.Root, destination, job.Plan.FoldersToCreate,
                    progress: p => _dispatcher.InvokeAsync(() =>
                        status.ShowCopyProgress(p, "Checking the project folders…", "Everything is already in the project.")),
                    cancellation: cancellation.Token);
            });
        }
        catch (Exception error)
        {
            failure = error;
        }

        _activeJob = null;
        _importCancel = null;
        RemoveTask(status);
        if (result is not null) ImportSucceeded(job, result);
        else ImportFailed(job, failure);
        _importStopReason = null;
        UpdateCanMove();
        StartNextImport();
    }

    private void ImportSucceeded(ImportJob job, BackupResult result)
    {
        var ledger = Library.Ledger(job.Volume.CardId);
        foreach (var file in result.ProcessedFiles) ledger.Record(file.Source, file.Destination);
        Library.SaveLedger(ledger, job.Volume.CardId);
        Library.MarkUsed(job.Projects);

        var copied = $"{Format.Count(result.FilesCopied, "file")} ({Format.Bytes(result.BytesCopied)})";
        var lines = new List<string>
        {
            $"Imported “{job.Volume.DisplayName}” into {job.Title}: {copied} copied, {result.FilesAlreadyBackedUp} already there, {result.Failures.Count} failed",
        };
        lines.AddRange(job.Projects.Select(p => $"    {p.Name}: {ProjectLayout.PhotoFolder(p)} | {ProjectLayout.VideoFolder(p)}"));
        lines.AddRange(result.Failures.Take(500).Select(f => $"    FAILED {f.RelativePath}: {f.Message}"));
        Log.Write(lines);
        HandOffPhotos(result);

        if (result.IsComplete)
        {
            Record(Outcome.Completed, job.Title, job.Volume, result, job.PrimaryFolder);
            Notify("Import complete", $"Copied {copied} from “{job.Volume.Name}” into {job.Title}.");
            var moreForThisCard = _importQueue.Any(j => j.Volume.Id == job.Volume.Id) || _drafts.Any(d => d.Volume.Id == job.Volume.Id);
            if (Settings.EjectWhenDone && !moreForThisCard) Eject(job.Volume, announce: true);
        }
        else
        {
            Record(Outcome.CompletedWithErrors, job.Title, job.Volume, result, job.PrimaryFolder);
            Notify("Import finished with problems",
                $"Copied {copied} into {job.Title}, but {Format.Count(result.Failures.Count, "file")} couldn’t be copied. Open the log for details.");
        }
    }

    /// <summary>
    /// Opens the folders that received new photos in the app chosen in Settings, which then shows its own
    /// import window. Never affects the import itself.
    /// </summary>
    private void HandOffPhotos(BackupResult result)
    {
        var app = Settings.PhotoApp;
        var folders = PhotoHandoff.Folders(result);
        if (app == PhotoApp.None || folders.Count == 0) return;
        var photos = Format.Count(PhotoHandoff.PhotoCount(result), "new photo");
        var title = PhotoApps.Title(app);
        var (outcome, error) = PhotoApps.Open(app, folders);
        switch (outcome)
        {
            case PhotoAppOpenResult.Opened:
                Log.Write($"Opened {photos} in {title}: {string.Join(", ", folders)}");
                break;
            case PhotoAppOpenResult.NotInstalled:
                Log.Write($"{title} isn’t installed, so the {photos} weren’t opened in it.");
                Notify($"{title} not found", $"Notch couldn’t find {title} on this PC, so the new photos weren’t opened in it. You can choose another app in Settings.");
                break;
            default:
                Log.Write($"Couldn’t open the {photos} in {title}: {error}");
                break;
        }
    }

    private void ImportFailed(ImportJob job, Exception? error)
    {
        Outcome outcome;
        string message;
        if (error is OperationCanceledException)
        {
            outcome = Outcome.Cancelled;
            message = _importStopReason ?? "Cancelled. Files copied so far were kept.";
        }
        else if (error is BackupException { Kind: BackupErrorKind.SourceDisappeared })
        {
            outcome = Outcome.Failed;
            message = "The card was removed before the import finished. Insert it again to finish.";
        }
        else
        {
            outcome = Outcome.Failed;
            message = error?.Message ?? "Unknown error.";
        }
        Log.Write($"Import of “{job.Volume.DisplayName}” into {job.Title} stopped: {message}");
        Record(outcome, job.Title, job.Volume, null, job.PrimaryFolder, message);
        Notify(outcome == Outcome.Cancelled ? "Import stopped" : "Import failed", message);
    }

    // ------------------------------------------------------------------ Moving a year

    /// <summary>Settings › Move a year: from the separate videos folder rather than the projects folder.</summary>
    public bool YearsFromVideos
    {
        get => _yearsFromVideos && Settings.HasSeparateVideosFolder;
        set
        {
            if (SetProperty(ref _yearsFromVideos, value)) RefreshYears();
        }
    }

    public IReadOnlyList<EnumChoice<bool>> YearSourceChoices { get; } = [new(false, "Photos folder"), new(true, "Videos folder")];

    public string NoYearsText => YearsFromVideos
        ? "There are no year folders in the videos folder yet."
        : "There are no year folders in the projects folder yet.";

    private string YearsRoot => YearsFromVideos ? Settings.SeparateVideosFolder! : Settings.ProjectsFolder;

    public void RefreshYears()
    {
        OnPropertyChanged(nameof(FolderStructureSummary));
        OnPropertyChanged(nameof(YearsFromVideos));
        OnPropertyChanged(nameof(NoYearsText));
        var years = YearMigrator.Years(YearsRoot);
        if (!Years.SequenceEqual(years))
        {
            Years.Clear();
            foreach (var year in years) Years.Add(year);
        }
        if (!Years.Contains(SelectedYear) && Years.Count > 0) SelectedYear = Years[0];
        OnPropertyChanged(nameof(HasYears));
        UpdateCanMove();
    }

    private void UpdateCanMove()
    {
        OnPropertyChanged(nameof(CanMoveYear));
        OnPropertyChanged(nameof(IsMigrating));
        OnPropertyChanged(nameof(MigrationStatus));
        OnPropertyChanged(nameof(IsBusy));
        ((RelayCommand)MoveYearCommand).RaiseCanExecuteChanged();
    }

    /// <summary>Asks where to move <paramref name="year"/>, confirms, then moves it in the background.</summary>
    public void MoveYear(int year)
    {
        if (_migrationStatus is not null) return;
        var yearText = year.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
        if (_activeJob is not null || _importQueue.Count > 0)
        {
            _dialogs.Alert("Wait for the import to finish", $"{yearText} can be moved once the current import is done.");
            return;
        }

        var destinationRoot = _dialogs.PickFolder(
            $"Choose a folder on the drive to move {yearText} to (for example a Projects folder on your SSD)",
            Settings.LastMigrationPath);
        if (destinationRoot is null) return;
        var sourceRoot = YearsRoot;
        if (PathUtil.AreSame(destinationRoot, sourceRoot))
        {
            _dialogs.Alert("Choose another folder", $"{yearText} is already in that folder. Pick a folder on the other drive.");
            return;
        }

        var driveName = DriveName(destinationRoot);
        var target = Path.Combine(destinationRoot, yearText);
        var confirmed = _dialogs.Confirm(
            $"Move {yearText} to “{driveName}”?",
            $"Everything in {Path.Combine(sourceRoot, yearText)}, including photos, videos and editing projects, is copied to {target}. Each file is checked against the original and then deleted from this PC.\n\n" +
            $"Projects from {yearText} will open from “{driveName}” from now on. New projects still go to this PC. " +
            "Editing projects that link to these files (Premiere Pro, DaVinci Resolve, …) may ask you to relink them once.",
            "Move");
        if (!confirmed) return;

        Settings.LastMigrationPath = destinationRoot;
        StartMigration(year, sourceRoot, destinationRoot, driveName);
    }

    private static string DriveName(string path)
    {
        var drive = LibraryLocator.DriveContaining(path);
        try
        {
            if (drive is { IsReady: true } && drive.VolumeLabel.Length > 0) return drive.VolumeLabel;
        }
        catch (IOException) { }
        return drive?.Name.TrimEnd('\\') ?? path;
    }

    private void CancelMigration()
    {
        _migrationStopReason = "Stopped. Files already moved are on the drive and the rest are still on this PC. Move the year again to finish.";
        _migrationCancel?.Cancel();
    }

    private async void StartMigration(int year, string sourceRoot, string destinationRoot, string driveName)
    {
        var yearText = year.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
        var status = new StatusViewModel(TaskKind.Moving, $"Moving {yearText}", $"To “{driveName}”", CancelMigrationCommand)
        {
            Note = "Each file is deleted from this PC once its copy is verified.",
        };
        _migrationStatus = status;
        _migrationStopReason = null;
        AddTask(status);
        UpdateCanMove();
        var cancellation = new CancellationTokenSource();
        _migrationCancel = cancellation;
        Log.Write($"Moving {yearText} from {sourceRoot} to {destinationRoot}");
        Notify($"Moving {yearText} to “{driveName}”", "Keep the drive connected until it’s finished.");

        MigrationResult? result = null;
        Exception? failure = null;
        try
        {
            result = await Task.Run(() =>
            {
                using var awake = KeepAwake.Begin();
                return YearMigrator.MoveYear(year, sourceRoot, destinationRoot,
                    p => _dispatcher.InvokeAsync(() => status.ShowCopyProgress(p, "Checking what’s already on the drive…", "Everything is already on the drive.")),
                    cancellation.Token);
            });
        }
        catch (Exception error)
        {
            failure = error;
        }

        _migrationStatus = null;
        _migrationCancel = null;
        RemoveTask(status);

        if (result is not null)
        {
            var relinked = Library.MoveProjects(year, sourceRoot, destinationRoot);
            var moved = $"{Format.Count(result.FilesMoved + result.DuplicatesRemoved, "file")} ({Format.Bytes(result.BytesMoved)})";
            var lines = new List<string>
            {
                $"Moved {yearText} to {result.Destination}: {result.FilesMoved} copied, {result.DuplicatesRemoved} already there, " +
                $"{result.Failures.Count} failed, {result.ItemsLeftBehind} left on this PC; {relinked} project(s) now open from {driveName}",
            };
            lines.AddRange(result.Failures.Take(500).Select(f => $"    FAILED {f.RelativePath}: {f.Message}"));
            Log.Write(lines);
            RecordMigration(result.IsComplete ? Outcome.Completed : Outcome.CompletedWithErrors, yearText, driveName, result.Destination, result, null);
            if (result.IsComplete)
            {
                Notify($"{yearText} moved to “{driveName}”", $"Moved {moved}. Projects from {yearText} now open from the drive.");
            }
            else
            {
                Notify($"{yearText} moved, with exceptions",
                    $"Moved {moved}. {Format.Count(result.Failures.Count + result.ItemsLeftBehind, "item")} stayed on this PC. Open the log for details.");
            }
        }
        else
        {
            var cancelled = failure is OperationCanceledException;
            var message = cancelled ? _migrationStopReason ?? "Stopped. Move the year again to finish." : failure?.Message ?? "Unknown error.";
            Log.Write($"Moving {yearText} stopped: {message}");
            RecordMigration(cancelled ? Outcome.Cancelled : Outcome.Failed, yearText, driveName, Path.Combine(destinationRoot, yearText), null, message);
            Notify(cancelled ? "Move stopped" : $"Couldn’t move {yearText}", message);
        }
        _migrationStopReason = null;
        RefreshYears();
        UpdateCanMove();
        StartNextImport();
    }

    // ------------------------------------------------------------------ History

    private HistoryEntry? LastImport(VolumeInfo volume) =>
        _history.FirstOrDefault(e => e.CardId == volume.CardId && e.Outcome is Outcome.Completed or Outcome.CompletedWithErrors);

    private void Record(Outcome outcome, string title, VolumeInfo volume, BackupResult? result, string? folder, string? message = null) =>
        AppendHistory(new HistoryEntry
        {
            Title = title,
            CardName = volume.Name,
            CardId = volume.CardId,
            Outcome = outcome,
            Kind = HistoryKind.CardImport,
            FilesCopied = result?.FilesCopied ?? 0,
            BytesCopied = result?.BytesCopied ?? 0,
            FilesAlreadyBackedUp = result?.FilesAlreadyBackedUp ?? 0,
            FailedFiles = result?.Failures.Count ?? 0,
            Message = message,
            FolderPath = folder,
        });

    private void RecordMigration(Outcome outcome, string yearText, string driveName, string folder, MigrationResult? result, string? message) =>
        AppendHistory(new HistoryEntry
        {
            Title = $"Moved {yearText}",
            CardName = driveName,
            Outcome = outcome,
            Kind = HistoryKind.Migration,
            FilesCopied = result is null ? 0 : result.FilesMoved + result.DuplicatesRemoved,
            BytesCopied = result?.BytesMoved ?? 0,
            FilesAlreadyBackedUp = result?.DuplicatesRemoved ?? 0,
            FailedFiles = result is null ? 0 : result.Failures.Count + result.ItemsLeftBehind,
            Message = message,
            FolderPath = folder,
        });

    private void AppendHistory(HistoryEntry entry)
    {
        _history.Insert(0, entry);
        if (_history.Count > HistoryLimit) _history.RemoveRange(HistoryLimit, _history.Count - HistoryLimit);
        if (_historyFile is not null)
        {
            try { Json.WriteFile(_historyFile, _history); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        RefreshRecent();
    }

    private void RefreshRecent()
    {
        Recent.Clear();
        foreach (var entry in _history.Take(3))
        {
            var e = entry;
            Recent.Add(new HistoryItemViewModel(entry, new RelayCommand(() => Reveal(e))));
        }
        OnPropertyChanged(nameof(HasRecent));
        SyncCards();
    }

    private void ClearHistory()
    {
        _history.Clear();
        if (_historyFile is not null)
        {
            try { File.Delete(_historyFile); } catch (IOException) { }
        }
        RefreshRecent();
    }

    // ------------------------------------------------------------------ Actions

    public async void Eject(VolumeInfo volume, bool announce)
    {
        var error = await Ejector.EjectAsync(volume.Root);
        if (error is null)
        {
            if (announce) Notify($"“{volume.Name}” ejected", "It’s safe to remove the card.");
        }
        else
        {
            Notify($"Couldn’t eject “{volume.Name}”", error);
        }
        RefreshCards();
    }

    private void OpenProjectsFolder()
    {
        var folder = Settings.ProjectsFolder;
        Shell.Open(Directory.Exists(folder) ? folder : Path.GetDirectoryName(folder) ?? folder);
    }

    private void Reveal(HistoryEntry entry)
    {
        if (entry.FolderPath is { } path && Directory.Exists(path)) Shell.Open(path);
        else OpenProjectsFolder();
    }

    private void OpenLog()
    {
        if (Log.FilePath is null) return;
        if (!File.Exists(Log.FilePath))
        {
            try { File.WriteAllText(Log.FilePath, ""); } catch (IOException) { }
        }
        Shell.Open(Log.FilePath);
    }

    // ------------------------------------------------------------------ Folder structure

    /// <summary>An editor for the structures new projects' photos and videos get (Settings › Folder structure…).</summary>
    public FolderStructuresEditor CreateFolderStructureEditor() =>
        new(FolderStructureFor(FolderStructure.Branch.Photo), FolderStructureFor(FolderStructure.Branch.Video), HasSeparateFolders, SaveFolderStructures);

    /// <summary>Saves the structures for new projects; alerts and returns false when a projects folder can't be written.</summary>
    private bool SaveFolderStructures(FolderStructure photo, FolderStructure video)
    {
        var photoRoot = DefaultRoot(FolderStructure.Branch.Photo);
        var videoRoot = DefaultRoot(FolderStructure.Branch.Video);
        if (!Library.SetStructure(photo, FolderStructure.Branch.Photo, photoRoot) || !Library.SetStructure(video, FolderStructure.Branch.Video, videoRoot))
        {
            var folders = HasSeparateFolders ? $"{photoRoot} / {videoRoot}" : photoRoot;
            _dialogs.Alert("The folder structure wasn’t saved",
                $"{folders} can’t be written. If it’s on an external drive, connect it and try again.");
            return false;
        }
        Log.Write(
            "Folder structure for new projects:",
            $"    Photos ({photoRoot}): {string.Join("\\", photo.ExamplePath(FolderStructure.Branch.Photo))}",
            $"    Videos ({videoRoot}): {string.Join("\\", video.ExamplePath(FolderStructure.Branch.Video))}");
        OnPropertyChanged(nameof(FolderStructureSummary));
        return true;
    }

    /// <summary>Settings › Videos: a separate projects folder for videos. New projects put their videos there.</summary>
    private void ChangeVideosFolder()
    {
        var folder = _dialogs.PickFolder("Choose a folder for videos. Videos of new projects go here, in the same folder structure as the photos.",
            Settings.SeparateVideosFolder ?? Settings.ProjectsFolder);
        if (folder is null) return;
        Settings.VideosFolder = PathUtil.AreSame(folder, Settings.ProjectsFolder) ? "" : folder;
        if (Settings.SeparateVideosFolder is { } videos) Library.Library(videos);
    }

    /// <summary>What import drafts ask this model for.</summary>
    private ImportDraftServices DraftServices => new(ChooseFolder, branch => Format.FolderName(PathUtil.Normalize(DefaultRoot(branch))), FinishShoot);

    /// <summary>
    /// Import panel › a new project's "Change…": asks for the folder its photos or videos go in. The folder of a project
    /// Notch knows adds the day to that project instead.
    /// </summary>
    private void ChooseFolder(ImportDraftViewModel draft, ShootDay day, FolderStructure.Branch branch)
    {
        string? folder;
        PanelDialogOpen?.Invoke(true);
        try
        {
            var title = branch == FolderStructure.Branch.Photo
                ? "Where should the photos go? Notch makes the project's folder (date and name) inside the folder you choose."
                : "Where should the videos go? Notch makes the project's folder (date and name) inside the folder you choose.";
            folder = _dialogs.PickFolder(title, draft.FolderFor(day, branch) ?? DefaultRoot(branch));
        }
        finally
        {
            PanelDialogOpen?.Invoke(false);
        }
        if (folder is null) return;
        Library.RefreshLocations();
        if (Library.ProjectAtFolder(folder) is { } project) draft.UseProject(project, day);
        else draft.SetFolder(day, branch, PathUtil.Normalize(folder));
    }

    // ------------------------------------------------------------------ Multi-day shoots

    /// <summary>Settings › Multi-day shoots.</summary>
    public ObservableCollection<ShootViewModel> Shoots { get; } = [];

    public bool HasShoots => Shoots.Count > 0;

    /// <summary>The import panel's or Settings' "hold to finish": the shoot leaves the import panel.</summary>
    public void FinishShoot(Guid id)
    {
        if (Library.Shoots.Shoot(id) is not { } shoot) return;
        Library.Shoots.Finish(id);
        Log.Write($"Finished the multi-day shoot “{shoot.Name}”");
    }

    public void ReopenShoot(Guid id) => Library.Shoots.Reopen(id);

    public void RefreshShoots()
    {
        Shoots.Clear();
        foreach (var shoot in Library.Shoots.All.OrderByDescending(s => s.LastUsed))
        {
            Shoots.Add(new ShootViewModel(shoot, new RelayCommand(() => FinishShoot(shoot.Id)), new RelayCommand(() => ReopenShoot(shoot.Id))));
        }
        OnPropertyChanged(nameof(HasShoots));
    }

    private void ChangeProjectsFolder()
    {
        var folder = _dialogs.PickFolder("Choose the projects folder. Year folders (2026, 2027, …) with your projects inside are created here.",
            Settings.ProjectsFolder);
        if (folder is null) return;
        Settings.ProjectsFolder = folder;
        Library.Library(folder);
    }

    private void ForgetImportedFiles()
    {
        if (_dialogs.Confirm("Forget which files were imported?",
                "Every file on every card will be offered for import again. Files that are already in a project folder are still skipped when you pick the same project.",
                "Forget"))
        {
            Library.ForgetImports();
        }
    }

    private void Quit()
    {
        if (IsBusy)
        {
            var (title, message) = _migrationStatus is not null
                ? ("A year is being moved", "If you quit now, files already moved stay on the drive and the rest stay on this PC. Move the year again to finish.")
                : (_activeJob is not null ? "An import is in progress" : "A card is being read",
                   "If you quit now, files copied so far are kept, and you can import the rest the next time the card is inserted.");
            if (!_dialogs.Confirm(title, message, "Quit")) return;
            _importCancel?.Cancel();
            _analysisCancel?.Cancel();
            _migrationCancel?.Cancel();
        }
        QuitRequested?.Invoke();
    }

    // ------------------------------------------------------------------ Helpers

    private void Notify(string title, string body)
    {
        if (Settings.NotificationsEnabled) _notify(title, body);
    }

    private void AddTask(StatusViewModel status, bool first = false)
    {
        if (first) Tasks.Insert(0, status);
        else Tasks.Add(status);
        UpdateIdleStatus();
    }

    private void RemoveTask(StatusViewModel status)
    {
        Tasks.Remove(status);
        UpdateIdleStatus();
    }

    private void DraftsChanged()
    {
        OnPropertyChanged(nameof(CurrentDraft));
        OnPropertyChanged(nameof(WaitingCount));
        OnPropertyChanged(nameof(MoreWaitingText));
        UpdateIdleStatus();
        SyncCards();
        WaitingCountChanged?.Invoke(_drafts.Count);
    }

    private void UpdateIdleStatus()
    {
        OnPropertyChanged(nameof(ShowIdle));
        OnPropertyChanged(nameof(IdleTitle));
        OnPropertyChanged(nameof(IdleSubtitle));
        OnPropertyChanged(nameof(IsBusy));
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _analysisCancel?.Cancel();
        _importCancel?.Cancel();
        _migrationCancel?.Cancel();
    }

    // ------------------------------------------------------------------ Self-test and previews

    /// <summary>Fills the model with sample data so every part of the UI shows (used by --self-test).</summary>
    public void LoadSampleState()
    {
        var volume = new VolumeInfo(@"E:\", "EOS_DIGITAL", "1A2B-3C4D", 64_000_000_000, true, false, true, true);
        _connected = [volume, volume with { Root = @"F:\", Label = "Untitled", Serial = "9C04-E1D7" }];

        SourceFile File(string path, long size, DateTime when) => new(Path.Combine(@"E:\", path), path, size, when, when);
        var day1 = new DateTime(2026, 11, 2, 10, 0, 0, DateTimeKind.Utc);
        var day2 = day1.AddDays(1);
        var items = new List<MediaItem>
        {
            new(File("DCIM/100CANON/IMG_0001.CR3", 30_000_000, day1), MediaKind.Photo, new ShootDay(2026, 11, 2), "Canon", "Canon EOS R5", []),
            new(File("DCIM/100CANON/MVI_0002.MP4", 900_000_000, day1), MediaKind.Video, new ShootDay(2026, 11, 2), "Canon", "Canon EOS R5", []),
            new(File("DCIM/101CANON/IMG_4410.CR3", 28_000_000, day2), MediaKind.Photo, new ShootDay(2026, 11, 3), "Canon", "Canon EOS R6", []),
            new(File("ZOOM0001.WAV", 120_000_000, day1), MediaKind.Audio, new ShootDay(2026, 11, 2), null, null, []),
        };
        var analysis = new CardAnalysis(items, 3, 1, []);
        var names = new Dictionary<string, string> { ["Canon EOS R5"] = "R5", ["Canon EOS R6"] = "R6", [""] = "Zoom H5" };
        var recent = new List<ProjectInfo>
        {
            new() { Name = "Product Shoot", StartDay = new ShootDay(2026, 10, 14), RootPath = Settings.ProjectsFolder },
            new() { Name = "Portraits Maja", StartDay = new ShootDay(2026, 10, 9), RootPath = "" },
        };
        Library.Shoots.Finish(Library.Shoots.Start("Poroka Novak 2025").Id);
        var shoot = Library.Shoots.Start("AMD Koledar 2026");
        var draft = new ImportDraftViewModel(volume, analysis, recent, [shoot], names, _ => { }, _ => { }, ImportDraftServices.None);
        draft.Days[0].ProjectName = "Ana Wedding";
        _drafts.Add(draft);
        _drafts.Add(draft);

        var importing = new StatusViewModel(TaskKind.Importing, "Importing", "EOS_DIGITAL → Ana Wedding", CancelImportCommand)
        {
            CopyStartedAt = DateTime.UtcNow.AddMinutes(-2),
        };
        importing.ShowCopyProgress(new BackupProgress(BackupPhase.Copying, 1950, 1204, 20_100_000_000, 12_400_000_000, 3, "DCIM/100CANON/IMG_1204.CR3"),
            "", "");
        Tasks.Add(importing);
        Tasks.Add(new StatusViewModel(TaskKind.Reading, "Reading card", "Untitled (F:)", CancelAnalysisCommand) { Line1Left = "Looking for photos and videos…" });

        _history.Clear();
        _history.Add(new HistoryEntry { Title = "Ana Wedding", CardName = "EOS_DIGITAL", CardId = "1A2B-3C4D", Outcome = Outcome.Completed, FilesCopied = 312, BytesCopied = 4_100_000_000, Date = DateTime.UtcNow.AddDays(-1) });
        _history.Add(new HistoryEntry { Title = "Moved 2025", CardName = "SSD", Outcome = Outcome.Completed, Kind = HistoryKind.Migration, FilesCopied = 8812, BytesCopied = 412_000_000_000, Date = DateTime.UtcNow.AddDays(-3) });
        _history.Add(new HistoryEntry { Title = "NO NAME", CardName = "NO NAME", Outcome = Outcome.CompletedWithErrors, FilesCopied = 88, FailedFiles = 2, Date = DateTime.UtcNow.AddDays(-9) });

        Years.Clear();
        Years.Add(2025);
        Years.Add(2026);
        SelectedYear = 2025;
        OnPropertyChanged(nameof(HasYears));
        DraftsChanged();
        RefreshRecent();
        SyncCards();
    }
}
