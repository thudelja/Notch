using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Input;
using SDBackup.Core;

namespace SDBackup.Windows;

// ------------------------------------------------------------------------------ Import panel

public enum DayChoiceKind
{
    Unassigned,
    /// <summary>A new project, in the photos' and videos' default places or in folders chosen for it.</summary>
    NewProject,
    /// <summary>A new project in an open multi-day shoot: it goes in the shoot's folder.</summary>
    NewInShoot,
    /// <summary>A new multi-day shoot, with this day's project as its first.</summary>
    NewShoot,
    /// <summary>The same new project as another day on this card.</summary>
    SameAs,
    Existing,
    Skip,
}

/// <summary>What the user picked for one shoot day.</summary>
public sealed record DayChoice(DayChoiceKind Kind, ShootDay? Owner = null, Guid? ProjectId = null, Guid? ShootId = null)
{
    public static readonly DayChoice Unassigned = new(DayChoiceKind.Unassigned);
    public static readonly DayChoice NewProject = new(DayChoiceKind.NewProject);
    public static readonly DayChoice NewShoot = new(DayChoiceKind.NewShoot);
    public static readonly DayChoice Skip = new(DayChoiceKind.Skip);
    public static DayChoice NewInShoot(Guid shootId) => new(DayChoiceKind.NewInShoot, ShootId: shootId);
    public static DayChoice SameAs(ShootDay owner) => new(DayChoiceKind.SameAs, Owner: owner);
    public static DayChoice Existing(Guid projectId) => new(DayChoiceKind.Existing, ProjectId: projectId);

    /// <summary>A new project of any kind; days can be "same as" it.</summary>
    public bool IsNewProject => Kind is DayChoiceKind.NewProject or DayChoiceKind.NewInShoot or DayChoiceKind.NewShoot;
}

/// <summary>What an import draft asks the app for: folder pickers, default folder names and finishing a shoot.</summary>
public sealed record ImportDraftServices(
    Action<ImportDraftViewModel, ShootDay, FolderStructure.Branch> ChooseFolder,
    Func<FolderStructure.Branch, string> DefaultPlaceName,
    Action<Guid> FinishShoot)
{
    public static readonly ImportDraftServices None = new((_, _, _) => { }, _ => "Projects", _ => { });
}

/// <summary>An entry in a day's project picker. Kept (and its title updated) across refreshes so the picker keeps its selection.</summary>
public sealed class ChoiceOption(DayChoice choice, string title) : ObservableObject
{
    private string _title = title;

    public DayChoice Choice { get; } = choice;

    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }
}

/// <summary>A card that has been read and is waiting for the user to pick a project for each shoot day.</summary>
public sealed class ImportDraftViewModel : ObservableObject
{
    private readonly Dictionary<ShootDay, DayChoice> _choices = new();
    private readonly Action<ImportDraftViewModel> _import;
    private readonly Action<ImportDraftViewModel> _dismiss;
    private readonly ImportDraftServices _services;
    private readonly List<ProjectInfo> _chosenProjects = [];
    private readonly Dictionary<ShootDay, string> _photoFolders = new();
    private readonly Dictionary<ShootDay, string> _videoFolders = new();
    private readonly HashSet<Guid> _finishedShoots = [];

    public ImportDraftViewModel(
        VolumeInfo volume,
        CardAnalysis analysis,
        IReadOnlyList<ProjectInfo> recentProjects,
        IReadOnlyList<MultiDayShoot> openShoots,
        IReadOnlyDictionary<string, string> cameraNames,
        Action<ImportDraftViewModel> import,
        Action<ImportDraftViewModel> dismiss,
        ImportDraftServices services)
    {
        Volume = volume;
        Analysis = analysis;
        RecentProjects = recentProjects;
        OpenShoots = openShoots;
        _import = import;
        _dismiss = dismiss;
        _services = services;
        HasPhotos = analysis.Items.Any(i => i.Kind == MediaKind.Photo);
        HasVideos = analysis.Items.Any(i => i.Kind != MediaKind.Photo);

        var days = analysis.Days;
        foreach (var summary in days)
        {
            // Most multi-day cards are one shoot: start with one new project for every day.
            _choices[summary.Day] = summary.Day == days[0].Day ? DayChoice.NewProject : DayChoice.SameAs(days[0].Day);
            Days.Add(new DayRowViewModel(this, summary));
        }

        var counts = analysis.Items.GroupBy(i => i.CameraModel ?? "").ToDictionary(g => g.Key, g => g.Count());
        var keys = analysis.CameraModels.Concat(analysis.HasUnknownCamera ? [""] : Array.Empty<string>());
        foreach (var key in keys)
        {
            var row = new CameraRowViewModel(key, counts.GetValueOrDefault(key), cameraNames.GetValueOrDefault(key) ?? Loc.T("Camera"));
            row.PropertyChanged += (_, _) => Changed();
            Cameras.Add(row);
        }

        ImportCommand = new RelayCommand(() => _import(this), () => IsReady);
        NotNowCommand = new RelayCommand(() => _dismiss(this));
        RefreshOptions();
    }

    public VolumeInfo Volume { get; }
    public CardAnalysis Analysis { get; }
    public IReadOnlyList<ProjectInfo> RecentProjects { get; }
    /// <summary>Multi-day shoots offered as "New in “…”".</summary>
    public IReadOnlyList<MultiDayShoot> OpenShoots { get; }
    public bool HasPhotos { get; }
    public bool HasVideos { get; }
    public ObservableCollection<DayRowViewModel> Days { get; } = [];
    public ObservableCollection<CameraRowViewModel> Cameras { get; } = [];
    public bool HasCameras => Cameras.Count > 0;
    public RelayCommand ImportCommand { get; }
    public ICommand NotNowCommand { get; }

    public string Title => Loc.F("Import “{0}”", Volume.Name);

    public string Summary
    {
        get
        {
            var parts = new List<string>
            {
                Format.Count(Days.Count, "shoot day"),
                Format.Count(Analysis.Items.Count, "new file"),
                Format.Bytes(Analysis.TotalBytes),
            };
            if (Analysis.AlreadyImported > 0) parts.Add(Loc.Plural(Analysis.AlreadyImported, "{0:N0} imported before", "{0:N0} imported before"));
            return string.Join(" · ", parts);
        }
    }

    public DayChoice ChoiceFor(ShootDay day) => _choices.GetValueOrDefault(day) ?? DayChoice.Unassigned;

    public void SetChoice(ShootDay day, DayChoice choice)
    {
        var previous = ChoiceFor(day);
        if (previous == choice) return;
        _choices[day] = choice;
        // Days that pointed at this day's new project need a new answer.
        if (previous.IsNewProject && !choice.IsNewProject)
        {
            foreach (var (other, value) in _choices.ToList())
            {
                if (value == DayChoice.SameAs(day)) _choices[other] = DayChoice.Unassigned;
            }
        }
        RefreshOptions();
    }

    internal void Changed()
    {
        RefreshOptions();
    }

    /// <summary>The user chose the folder of an existing project for this day.</summary>
    public void UseProject(ProjectInfo project, ShootDay day)
    {
        if (!KnownProjects.Any(p => p.Id == project.Id)) _chosenProjects.Add(project);
        _choices[day] = DayChoice.Existing(project.Id);
        RefreshOptions();
    }

    /// <summary>A new project's "Change…": asks for the folder its photos or videos go in.</summary>
    internal void ChooseFolder(ShootDay day, FolderStructure.Branch branch)
    {
        _services.ChooseFolder(this, day, branch);
        RefreshOptions();
    }

    /// <summary>A folder chosen for a new project's photos or videos; null goes back to the default place.</summary>
    public void SetFolder(ShootDay day, FolderStructure.Branch branch, string? folder)
    {
        var folders = branch == FolderStructure.Branch.Photo ? _photoFolders : _videoFolders;
        if (folder is null) folders.Remove(day);
        else folders[day] = folder;
        RefreshOptions();
    }

    public string? FolderFor(ShootDay day, FolderStructure.Branch branch) =>
        (branch == FolderStructure.Branch.Photo ? _photoFolders : _videoFolders).GetValueOrDefault(day);

    /// <summary>"Photos → Projects": where a new project's photos or videos go.</summary>
    internal string PlaceText(ShootDay day, FolderStructure.Branch branch)
    {
        var place = FolderFor(day, branch) is { } folder ? Format.FolderName(folder) : _services.DefaultPlaceName(branch);
        return branch == FolderStructure.Branch.Photo ? Loc.F("Photos → {0}", place) : Loc.F("Videos → {0}", place);
    }

    public MultiDayShoot? Shoot(Guid id) => OpenShoots.FirstOrDefault(s => s.Id == id);

    /// <summary>Finishes a shoot from the panel. It still takes this import; every day in it shows it as finished.</summary>
    internal void FinishShoot(Guid id)
    {
        if (!_finishedShoots.Add(id)) return;
        _services.FinishShoot(id);
        RefreshOptions();
    }

    public bool IsFinished(Guid shootId) => _finishedShoots.Contains(shootId);

    /// <summary>Multi-day shoots this import starts, and those it adds to (by name).</summary>
    public (IReadOnlyList<string> Started, IReadOnlyList<string> Continued) ShootsUsed
    {
        get
        {
            var started = new List<string>();
            var continued = new List<string>();
            foreach (var row in Days)
            {
                var choice = ChoiceFor(row.Day);
                if (choice.Kind == DayChoiceKind.NewShoot) started.Add(row.ShootName.Trim());
                if (choice is { Kind: DayChoiceKind.NewInShoot, ShootId: Guid id } && Shoot(id) is { } shoot) continued.Add(shoot.Name);
            }
            return (started, continued);
        }
    }

    private IEnumerable<ProjectInfo> KnownProjects => RecentProjects.Concat(_chosenProjects);

    private void RefreshOptions()
    {
        foreach (var row in Days) row.Refresh(OptionsFor(row.Day));
        OnPropertyChanged(nameof(BlockingReason));
        OnPropertyChanged(nameof(IsReady));
        ImportCommand.RaiseCanExecuteChanged();
    }

    private List<(DayChoice Choice, string Title)> OptionsFor(ShootDay day)
    {
        var options = new List<(DayChoice Choice, string Title)>
        {
            (DayChoice.Unassigned, Loc.T("Choose a project…")),
            (DayChoice.NewProject, Loc.T("New project")),
        };
        foreach (var shoot in OpenShoots) options.Add((DayChoice.NewInShoot(shoot.Id), Loc.F("New in “{0}”", shoot.Name)));
        options.Add((DayChoice.NewShoot, Loc.T("New multi-day shoot…")));
        foreach (var row in Days)
        {
            if (row.Day != day && ChoiceFor(row.Day).IsNewProject)
            {
                options.Add((DayChoice.SameAs(row.Day), SameAsTitle(row.Day)));
            }
        }
        foreach (var project in KnownProjects)
        {
            options.Add((DayChoice.Existing(project.Id), IsOnConnectedDrive(project)
                ? $"{project.Name} · {project.StartDay.FolderDate}"
                : Loc.F("{0} · {1} (drive not connected)", project.Name, project.StartDay.FolderDate)));
        }
        options.Add((DayChoice.Skip, Loc.T("Skip for now")));
        return options;
    }

    /// <summary>"Same as “Ana” (new)", or "Same as the new project on 02.11.2026" before it has a name.</summary>
    private string SameAsTitle(ShootDay owner)
    {
        var name = NameFor(owner);
        return name.Length == 0 ? Loc.F("Same as the new project on {0}", owner.FolderDate) : Loc.F("Same as “{0}” (new)", name);
    }

    internal string NameFor(ShootDay day) => Days.FirstOrDefault(d => d.Day == day)?.ProjectName.Trim() ?? "";

    private static bool IsOnConnectedDrive(ProjectInfo project) =>
        Exists(project.RootPath) && (!project.HasSeparateVideoFolder || Exists(project.VideoRoot));

    /// <summary>Whether the folders this day's files go to are connected: the photos' and the videos'.</summary>
    private static bool IsAvailable(ProjectInfo project, DaySummary summary) =>
        (summary.Photos == 0 || Exists(project.RootPath)) && (summary.Videos + summary.Audio == 0 || Exists(project.VideoRoot));

    private static bool Exists(string path) => path.Length > 0 && System.IO.Directory.Exists(path);

    public bool IsReady => BlockingReason is null;

    /// <summary>Why Import is disabled, if it is.</summary>
    public string? BlockingReason
    {
        get
        {
            var importsSomething = false;
            foreach (var row in Days)
            {
                var choice = ChoiceFor(row.Day);
                switch (choice.Kind)
                {
                    case DayChoiceKind.Unassigned:
                        return Loc.F("Choose a project for {0}.", row.Day.FolderDate);
                    case DayChoiceKind.Skip:
                        continue;
                    case DayChoiceKind.NewShoot when row.ShootName.Trim().Length == 0:
                        return Loc.F("Type a name for the multi-day shoot ({0}).", row.Day.FolderDate);
                    case DayChoiceKind.NewProject or DayChoiceKind.NewInShoot or DayChoiceKind.NewShoot when NameFor(row.Day).Length == 0:
                        return Loc.F("Type a name for the new project ({0}).", row.Day.FolderDate);
                    case DayChoiceKind.SameAs when choice.Owner is ShootDay owner
                                                && (!ChoiceFor(owner).IsNewProject || NameFor(owner).Length == 0):
                        return Loc.F("Type a name for the new project ({0}).", owner.FolderDate);
                    case DayChoiceKind.Existing:
                        var project = KnownProjects.FirstOrDefault(p => p.Id == choice.ProjectId);
                        if (project is not null && !IsAvailable(project, row.Summary))
                        {
                            return Loc.F("“{0}” is on a drive that isn’t connected. Connect it or choose another project.", project.Name);
                        }
                        break;
                }
                importsSomething = true;
            }
            return importsSomething ? null : Loc.T("Every date is skipped.");
        }
    }

    /// <summary>
    /// The project for every day that isn't skipped. A new project starts on its earliest day; its photos and videos go to
    /// their default places (<paramref name="photosRoot"/>, <paramref name="videosRoot"/>) or the folders chosen for them,
    /// and into its shoot's folder for a multi-day shoot. Null if a folder isn't available.
    /// </summary>
    public IReadOnlyDictionary<ShootDay, ProjectInfo>? ResolveProjects(LibraryStore store, string photosRoot, string videosRoot)
    {
        var result = new Dictionary<ShootDay, ProjectInfo>();
        var newProjectDays = new Dictionary<ShootDay, List<ShootDay>>();
        foreach (var row in Days)
        {
            var choice = ChoiceFor(row.Day);
            switch (choice.Kind)
            {
                case DayChoiceKind.NewProject or DayChoiceKind.NewInShoot or DayChoiceKind.NewShoot:
                    AddTo(newProjectDays, row.Day, row.Day);
                    break;
                case DayChoiceKind.SameAs when choice.Owner is ShootDay owner:
                    AddTo(newProjectDays, owner, row.Day);
                    break;
                case DayChoiceKind.Existing when choice.ProjectId is Guid id && store.Project(id) is { } project:
                    result[row.Day] = project;
                    break;
            }
        }
        foreach (var (owner, members) in newProjectDays)
        {
            var ownerChoice = ChoiceFor(owner);
            string? series = ownerChoice switch
            {
                { Kind: DayChoiceKind.NewInShoot, ShootId: Guid id } => Shoot(id)?.Name,
                { Kind: DayChoiceKind.NewShoot } => Days.FirstOrDefault(d => d.Day == owner)?.ShootName.Trim(),
                _ => null,
            };
            if (!ownerChoice.IsNewProject) continue;
            // Folders can be chosen for a plain new project; a shoot's projects go in the shoot's folder.
            var isPlain = ownerChoice.Kind == DayChoiceKind.NewProject;
            var project = store.NewProject(NameFor(owner), members.Min(), series,
                new LibraryStore.Place(photosRoot, isPlain ? FolderFor(owner, FolderStructure.Branch.Photo) : null),
                new LibraryStore.Place(videosRoot, isPlain ? FolderFor(owner, FolderStructure.Branch.Video) : null));
            if (project is null) return null;
            foreach (var day in members) result[day] = project;
        }
        return result;

        static void AddTo(Dictionary<ShootDay, List<ShootDay>> map, ShootDay key, ShootDay day)
        {
            if (!map.TryGetValue(key, out var list)) map[key] = list = [];
            list.Add(day);
        }
    }

    public IReadOnlyDictionary<string, string> CameraNames => Cameras.ToDictionary(c => c.Key, c => c.Name);

    public string CameraNameFor(MediaItem item)
    {
        var name = Cameras.FirstOrDefault(c => c.Key == (item.CameraModel ?? ""))?.Name.Trim() ?? "";
        return name.Length == 0 ? Loc.T("Camera") : name;
    }
}

/// <summary>One shoot day in the import panel.</summary>
public sealed class DayRowViewModel : ObservableObject
{
    private readonly ImportDraftViewModel _owner;
    private string _projectName = "";
    private string _shootName = "";
    private bool _refreshing;

    public DayRowViewModel(ImportDraftViewModel owner, DaySummary summary)
    {
        _owner = owner;
        Summary = summary;
        ChangePhotosCommand = new RelayCommand(() => owner.ChooseFolder(Day, FolderStructure.Branch.Photo));
        ChangeVideosCommand = new RelayCommand(() => owner.ChooseFolder(Day, FolderStructure.Branch.Video));
        ResetPhotosCommand = new RelayCommand(() => owner.SetFolder(Day, FolderStructure.Branch.Photo, null));
        ResetVideosCommand = new RelayCommand(() => owner.SetFolder(Day, FolderStructure.Branch.Video, null));
        FinishShootCommand = new RelayCommand(FinishShoot);
    }

    public DaySummary Summary { get; }
    public ShootDay Day => Summary.Day;
    public string DateText => Day.FolderDate;
    public string Weekday => Day.ToDateTime().ToString("ddd", CultureInfo.CurrentCulture);

    public string Counts
    {
        get
        {
            var parts = new List<string>();
            if (Summary.Photos > 0) parts.Add(Format.Count(Summary.Photos, "photo"));
            if (Summary.Videos > 0) parts.Add(Format.Count(Summary.Videos, "video"));
            if (Summary.Audio > 0) parts.Add(Format.Count(Summary.Audio, "audio", "audio"));
            parts.Add(Format.Bytes(Summary.Bytes));
            return string.Join(" · ", parts);
        }
    }

    public ObservableCollection<ChoiceOption> Options { get; } = [];

    /// <summary>Bound to the project picker's SelectedValue.</summary>
    public DayChoice? Choice
    {
        get => _owner.ChoiceFor(Day);
        set
        {
            // The picker reports null when its selected option is removed during a refresh.
            if (value is null || _refreshing) return;
            _owner.SetChoice(Day, value);
        }
    }

    public bool IsNewProject => _owner.ChoiceFor(Day).IsNewProject;
    public bool IsNewShoot => _owner.ChoiceFor(Day).Kind == DayChoiceKind.NewShoot;
    public bool IsInShoot => _owner.ChoiceFor(Day).Kind == DayChoiceKind.NewInShoot;

    /// <summary>A plain new project: its photos' and videos' folders can be changed.</summary>
    public bool IsPlainNew => _owner.ChoiceFor(Day).Kind == DayChoiceKind.NewProject;

    /// <summary>The row asks for the new project's name (a new shoot asks for it with the shoot's name).</summary>
    public bool AsksForName => IsPlainNew || IsInShoot;
    public bool ShowsPhotoPlace => IsPlainNew && _owner.HasPhotos;
    public bool ShowsVideoPlace => IsPlainNew && _owner.HasVideos;
    public string PhotoPlaceText => _owner.PlaceText(Day, FolderStructure.Branch.Photo);
    public string VideoPlaceText => _owner.PlaceText(Day, FolderStructure.Branch.Video);
    public bool HasChosenPhotoFolder => _owner.FolderFor(Day, FolderStructure.Branch.Photo) is not null;
    public bool HasChosenVideoFolder => _owner.FolderFor(Day, FolderStructure.Branch.Video) is not null;

    public ICommand ChangePhotosCommand { get; }
    public ICommand ChangeVideosCommand { get; }
    public ICommand ResetPhotosCommand { get; }
    public ICommand ResetVideosCommand { get; }

    /// <summary>The name of a new multi-day shoot started on this day.</summary>
    public string ShootName
    {
        get => _shootName;
        set
        {
            if (SetProperty(ref _shootName, value ?? "")) _owner.Changed();
        }
    }

    private MultiDayShoot? ChosenShoot => _owner.ChoiceFor(Day) is { Kind: DayChoiceKind.NewInShoot, ShootId: Guid id } ? _owner.Shoot(id) : null;

    public string FinishShootTitle => ChosenShoot is { } shoot ? Loc.F("Hold to finish “{0}”", shoot.Name) : "";
    public string ShootFinishedText => ChosenShoot is { } shoot ? Loc.F("“{0}” is finished after this import", shoot.Name) : "";

    /// <summary>The chosen shoot was finished in this panel (it still takes this import).</summary>
    public bool ShootFinished => ChosenShoot is { } shoot && _owner.IsFinished(shoot.Id);

    public bool CanFinishShoot => IsInShoot && !ShootFinished;

    /// <summary>The hold-to-finish button's command (it runs after the button was held).</summary>
    public ICommand FinishShootCommand { get; }

    private void FinishShoot()
    {
        if (ChosenShoot is { } shoot) _owner.FinishShoot(shoot.Id);
    }

    public string ProjectName
    {
        get => _projectName;
        set
        {
            if (SetProperty(ref _projectName, value ?? "")) _owner.Changed();
        }
    }

    /// <summary>
    /// Updates the picker's options in place: entries that stay keep their identity (only titles change), so
    /// the picker keeps showing the selected one. Replacing the list would make it lose the selection.
    /// </summary>
    internal void Refresh(IReadOnlyList<(DayChoice Choice, string Title)> wanted)
    {
        _refreshing = true;
        for (var i = Options.Count - 1; i >= 0; i--)
        {
            if (!wanted.Any(w => w.Choice == Options[i].Choice)) Options.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var (choice, title) = wanted[i];
            var existing = Options.FirstOrDefault(o => o.Choice == choice);
            if (existing is null)
            {
                Options.Insert(i, new ChoiceOption(choice, title));
                continue;
            }
            existing.Title = title;
            var index = Options.IndexOf(existing);
            if (index != i) Options.Move(index, i);
        }
        _refreshing = false;
        foreach (var name in new[]
                 {
                     nameof(Choice), nameof(IsNewProject), nameof(IsNewShoot), nameof(IsInShoot), nameof(IsPlainNew), nameof(AsksForName),
                     nameof(ShowsPhotoPlace), nameof(ShowsVideoPlace), nameof(PhotoPlaceText), nameof(VideoPlaceText),
                     nameof(HasChosenPhotoFolder), nameof(HasChosenVideoFolder), nameof(FinishShootTitle),
                     nameof(ShootFinishedText), nameof(ShootFinished), nameof(CanFinishShoot),
                 })
        {
            OnPropertyChanged(name);
        }
    }
}

/// <summary>A camera folder name the user can edit.</summary>
public sealed class CameraRowViewModel(string key, int fileCount, string name) : ObservableObject
{
    private string _name = name;

    /// <summary>The camera model, or "" for files without camera information.</summary>
    public string Key { get; } = key;
    public string Label => Key.Length == 0 ? Loc.T("No camera info") : Key;
    public string CountText => Format.Count(fileCount, "file");

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, value ?? "");
    }
}

// ------------------------------------------------------------------------------ Status and lists

public enum TaskKind
{
    Reading,
    Importing,
    Moving,
}

/// <summary>A running task shown in the panel: reading a card, importing, or moving a year.</summary>
public sealed class StatusViewModel(TaskKind kind, string title, string subtitle, ICommand cancel) : ObservableObject
{
    private double _progress;
    private bool _isIndeterminate = true;
    private string _line1Left = "", _line1Right = "", _line2Left = "", _line2Right = "", _currentFile = "", _note = "";

    public TaskKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public ICommand CancelCommand { get; } = cancel;
    public DateTime? CopyStartedAt { get; set; }

    /// <summary>0 to 100.</summary>
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
    public bool IsIndeterminate { get => _isIndeterminate; set => SetProperty(ref _isIndeterminate, value); }
    public string Line1Left { get => _line1Left; set => SetProperty(ref _line1Left, value); }
    public string Line1Right { get => _line1Right; set => SetProperty(ref _line1Right, value); }
    public string Line2Left { get => _line2Left; set => SetProperty(ref _line2Left, value); }
    public string Line2Right { get => _line2Right; set => SetProperty(ref _line2Right, value); }
    public string CurrentFile { get => _currentFile; set => SetProperty(ref _currentFile, value); }
    public string Note { get => _note; set => SetProperty(ref _note, value); }

    /// <summary>Shows copy progress: sizes, time left, files and percentage.</summary>
    public void ShowCopyProgress(BackupProgress progress, string checkingText, string nothingToCopyText)
    {
        if (progress.Phase == BackupPhase.Scanning)
        {
            IsIndeterminate = true;
            Line1Left = checkingText;
            Line1Right = Line2Left = Line2Right = CurrentFile = "";
            return;
        }
        CopyStartedAt ??= DateTime.UtcNow;
        IsIndeterminate = false;
        Progress = progress.FractionCompleted * 100;
        if (progress.FilesToCopy == 0)
        {
            Line1Left = nothingToCopyText;
            Line1Right = Line2Left = Line2Right = CurrentFile = "";
            return;
        }
        Line1Left = Loc.F("{0} of {1}", Format.Bytes(progress.BytesProcessed), Format.Bytes(progress.BytesToCopy));
        Line1Right = TimeLeft(progress) is TimeSpan left ? Loc.F("{0} left", Format.Duration(left)) : "";
        Line2Left = Loc.Plural(progress.FilesToCopy, "{1:N0} of {0:N0} file", "{1:N0} of {0:N0} files", progress.FilesProcessed);
        Line2Right = $"{Math.Round(progress.FractionCompleted * 100):0}%";
        CurrentFile = progress.CurrentFile ?? "";
    }

    private TimeSpan? TimeLeft(BackupProgress progress)
    {
        if (CopyStartedAt is not DateTime start || progress.BytesProcessed <= 0) return null;
        var elapsed = DateTime.UtcNow - start;
        if (elapsed < TimeSpan.FromSeconds(3)) return null;
        var bytesPerSecond = progress.BytesProcessed / elapsed.TotalSeconds;
        return TimeSpan.FromSeconds(Math.Max(0, progress.BytesToCopy - progress.BytesProcessed) / bytesPerSecond);
    }
}

public enum CardStatus
{
    Idle,
    Reading,
    NeedsProject,
    Queued,
    Importing,
}

/// <summary>A connected card in the panel.</summary>
public sealed class CardViewModel(VolumeInfo volume, ICommand import, ICommand eject) : ObservableObject
{
    private CardStatus _status;
    private string _detail = "";

    public VolumeInfo Volume { get; private set; } = volume;
    public string Name => Volume.DisplayName;
    public ICommand ImportCommand { get; } = import;
    public ICommand EjectCommand { get; } = eject;

    public CardStatus Status
    {
        get => _status;
        set
        {
            if (!SetProperty(ref _status, value)) return;
            OnPropertyChanged(nameof(CanImport));
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanEject));
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public bool CanImport => Status == CardStatus.Idle;
    public bool IsBusy => Status != CardStatus.Idle;
    public bool CanEject => Status != CardStatus.Importing;

    public string StatusText => Status switch
    {
        CardStatus.Reading => Loc.T("Reading…"),
        CardStatus.NeedsProject => Loc.T("Choose project"),
        CardStatus.Queued => Loc.T("Queued"),
        CardStatus.Importing => Loc.T("Importing"),
        _ => "",
    };

    internal void Update(VolumeInfo volume)
    {
        Volume = volume;
        OnPropertyChanged(nameof(Name));
    }
}

public enum Outcome
{
    Completed,
    CompletedWithErrors,
    Failed,
    Cancelled,
}

public enum HistoryKind
{
    CardImport,
    Migration,
}

/// <summary>A finished import or year move, shown in the panel and saved between launches.</summary>
public sealed record HistoryEntry
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; init; } = "";
    public string CardName { get; init; } = "";
    public string CardId { get; init; } = "";
    public DateTime Date { get; init; } = DateTime.UtcNow;
    [JsonConverter(typeof(JsonStringEnumConverter))] public Outcome Outcome { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))] public HistoryKind Kind { get; init; }
    public int FilesCopied { get; init; }
    public long BytesCopied { get; init; }
    public int FilesAlreadyBackedUp { get; init; }
    public int FailedFiles { get; init; }
    public string? Message { get; init; }
    public string? FolderPath { get; init; }

    [JsonIgnore]
    public string Summary => (Kind, Outcome) switch
    {
        (HistoryKind.Migration, Outcome.Completed) => Loc.F("{0} · {1} · to {2}", Format.Count(FilesCopied, "file"), Format.Bytes(BytesCopied), CardName),
        (HistoryKind.Migration, Outcome.CompletedWithErrors) =>
            Loc.Plural(FilesCopied, "{0:N0} file moved to {1}", "{0:N0} files moved to {1}", CardName) + " · "
            + Loc.Plural(FailedFiles, "{0:N0} stayed on this PC", "{0:N0} stayed on this PC"),
        (HistoryKind.Migration, _) => Message ?? Loc.T("Move stopped"),
        (_, Outcome.Completed) when FilesCopied == 0 => Loc.F("Already there · from {0}", CardName),
        (_, Outcome.Completed) => Loc.F("{0} · {1} · from {2}", Format.Count(FilesCopied, "file"), Format.Bytes(BytesCopied), CardName),
        (_, Outcome.CompletedWithErrors) =>
            Loc.Plural(FilesCopied, "{0:N0} file copied", "{0:N0} files copied") + " · "
            + Loc.Plural(FailedFiles, "{0:N0} couldn’t be copied", "{0:N0} couldn’t be copied"),
        _ => Message ?? Loc.T("Import stopped"),
    };
}

public sealed class HistoryItemViewModel(HistoryEntry entry, ICommand open) : ObservableObject
{
    public HistoryEntry Entry { get; } = entry;
    public string Title => Entry.Title;
    public string Summary => Entry.Summary;
    public string When => Relative.Format(Entry.Date);
    public Outcome Outcome => Entry.Outcome;
    public bool IsMigration => Entry.Kind == HistoryKind.Migration;
    public ICommand OpenCommand { get; } = open;
}

public static class Relative
{
    /// <summary>"just now", "5 minutes ago", "yesterday", "3 days ago", or a date.</summary>
    public static string Format(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span < TimeSpan.FromMinutes(1)) return Loc.T("just now");
        if (span < TimeSpan.FromHours(1)) return Loc.Plural((int)span.TotalMinutes, "{0} minute ago", "{0} minutes ago");
        if (span < TimeSpan.FromDays(1)) return Loc.Plural((int)span.TotalHours, "{0} hour ago", "{0} hours ago");
        if (span < TimeSpan.FromDays(2)) return Loc.T("yesterday");
        if (span < TimeSpan.FromDays(7)) return Loc.Plural((int)span.TotalDays, "{0} day ago", "{0} days ago");
        return utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.CurrentCulture);
    }
}
