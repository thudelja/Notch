using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SDBackup.Core;

namespace SDBackup.Windows;

/// <summary>An entry in one of the editor's pickers.</summary>
public sealed record EnumChoice<T>(T Value, string Title);

/// <summary>
/// Settings › Folder structure…: edits how new projects in the projects folder are laid out below the year folder
/// (PORTING.md §2.1). The window binds to this; <see cref="Structure"/> is the result.
/// </summary>
public sealed class FolderStructureEditor : ObservableObject
{
    private readonly FolderStructure _original;
    private readonly Func<FolderStructure, bool> _save;
    private bool _cameraFolders;
    private MediaChoice? _selectedMedia;
    private bool _loading;

    /// <param name="structure">The structure to start from.</param>
    /// <param name="save">Saves the structure; false keeps the window open (the projects folder couldn't be written).</param>
    public FolderStructureEditor(FolderStructure structure, Func<FolderStructure, bool> save)
    {
        _original = structure;
        _save = save;
        AddMediaTypeCommand = new RelayCommand(() => Add(FolderLevel.MediaTypeLevel()), () => !Has(FolderLevel.LevelKind.MediaType));
        AddMonthCommand = new RelayCommand(() => Add(FolderLevel.MonthLevel()), () => !Has(FolderLevel.LevelKind.Month));
        AddFolderCommand = new RelayCommand(() => Add(FolderLevel.TextLevel("Folder")));
        AddSplitCommand = new RelayCommand(() => Add(FolderLevel.SplitLevel()), () => !Has(FolderLevel.LevelKind.Split));
        AddVideoFolderCommand = new RelayCommand(() => AddVideoFolder(""));
        ResetCommand = new RelayCommand(() => Load(FolderStructure.Standard), () => !Structure.IsStandard);
        SaveCommand = new RelayCommand(Save, () => CanSave);
        Load(structure);
    }

    public ObservableCollection<FolderLevelViewModel> Levels { get; } = [];
    public ObservableCollection<VideoFolderViewModel> VideoFolders { get; } = [];

    /// <summary>The video folders, and "The project folder": where videos and audio go.</summary>
    public ObservableCollection<MediaChoice> MediaChoices { get; } = [];

    public bool CameraFolders
    {
        get => _cameraFolders;
        set
        {
            if (SetProperty(ref _cameraFolders, value)) Changed();
        }
    }

    public MediaChoice? SelectedMedia
    {
        get => _selectedMedia;
        set
        {
            // A picker clears its selection while its list changes; keep the choice.
            if (value is null) return;
            if (SetProperty(ref _selectedMedia, value)) Changed();
        }
    }

    /// <summary>The structure as edited.</summary>
    public FolderStructure Structure => new()
    {
        Levels = Levels.Select(l => l.Level).ToList(),
        CameraFolders = CameraFolders,
        VideoFolders = VideoFolders.Select(f => f.Name).ToList(),
        MediaFolder = SelectedMedia?.Folder?.Name ?? "",
    };

    public IReadOnlyList<FolderExample> Examples => Structure.Examples("\\");
    public IReadOnlyList<string> Problems => Structure.Problems;
    public bool HasProblems => Problems.Count > 0;
    public bool CanSave => Structure is { IsValid: true } structure && !structure.Equals(_original);

    public ICommand AddMediaTypeCommand { get; }
    public ICommand AddMonthCommand { get; }
    public ICommand AddFolderCommand { get; }
    public ICommand AddSplitCommand { get; }
    public ICommand AddVideoFolderCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand SaveCommand { get; }

    /// <summary>Raised after a successful save, so the window closes.</summary>
    public event Action? Saved;

    // ------------------------------------------------------------------ Editing

    internal void Changed()
    {
        if (_loading) return;
        OnPropertyChanged(nameof(Examples));
        OnPropertyChanged(nameof(Problems));
        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(CanSave));
        foreach (var command in new[] { AddMediaTypeCommand, AddMonthCommand, AddSplitCommand, ResetCommand, SaveCommand })
        {
            ((RelayCommand)command).RaiseCanExecuteChanged();
        }
        foreach (var level in Levels) level.RefreshCommands();
    }

    internal bool CanMove(FolderLevelViewModel level, int offset)
    {
        var target = Levels.IndexOf(level) + offset;
        return target >= 0 && target < Levels.Count;
    }

    internal void Move(FolderLevelViewModel level, int offset)
    {
        if (!CanMove(level, offset)) return;
        var index = Levels.IndexOf(level);
        Levels.Move(index, index + offset);
        Changed();
    }

    internal void Remove(FolderLevelViewModel level)
    {
        if (!level.CanRemove) return;
        Levels.Remove(level);
        Changed();
    }

    internal void Remove(VideoFolderViewModel folder)
    {
        var choice = MediaChoices.FirstOrDefault(c => c.Folder == folder);
        if (choice is not null && SelectedMedia == choice) SelectedMedia = MediaChoices[^1];
        if (choice is not null) MediaChoices.Remove(choice);
        VideoFolders.Remove(folder);
        Changed();
    }

    private bool Has(FolderLevel.LevelKind kind) => Levels.Any(l => l.Kind == kind);

    /// <summary>New levels go just above the project folder; they can be moved from there.</summary>
    private void Add(FolderLevel level)
    {
        var project = Levels.ToList().FindIndex(l => l.Kind == FolderLevel.LevelKind.Project);
        Levels.Insert(project < 0 ? Levels.Count : project, new FolderLevelViewModel(this, level));
        Changed();
    }

    private VideoFolderViewModel AddVideoFolder(string name)
    {
        var folder = new VideoFolderViewModel(this, name);
        VideoFolders.Add(folder);
        // Before the last choice, "The project folder".
        MediaChoices.Insert(Math.Max(0, MediaChoices.Count - 1), new MediaChoice(folder));
        Changed();
        return folder;
    }

    private void Load(FolderStructure structure)
    {
        _loading = true;
        Levels.Clear();
        foreach (var level in structure.Levels) Levels.Add(new FolderLevelViewModel(this, level));
        CameraFolders = structure.CameraFolders;
        VideoFolders.Clear();
        MediaChoices.Clear();
        MediaChoices.Add(new MediaChoice(null));
        foreach (var name in structure.VideoFolders) AddVideoFolder(name);
        _selectedMedia = MediaChoices.FirstOrDefault(c =>
            c.Folder is { } folder && structure.MediaFolder.Length > 0 && string.Equals(folder.Name, structure.MediaFolder, StringComparison.OrdinalIgnoreCase))
            ?? MediaChoices[^1];
        _loading = false;
        OnPropertyChanged(nameof(SelectedMedia));
        Changed();
    }

    private void Save()
    {
        if (CanSave && _save(Structure)) Saved?.Invoke();
    }
}

/// <summary>One level of the structure in the editor.</summary>
public sealed class FolderLevelViewModel : ObservableObject
{
    private static readonly IReadOnlyList<EnumChoice<FolderLevel.MonthStyles>> s_monthStyles =
    [
        new(FolderLevel.MonthStyles.NumberAndName, "11 November"),
        new(FolderLevel.MonthStyles.Number, "11"),
        new(FolderLevel.MonthStyles.Name, "November"),
    ];

    private static readonly IReadOnlyList<EnumChoice<FolderLevel.MonthLanguages>> s_monthLanguages =
    [
        new(FolderLevel.MonthLanguages.English, "English names"),
        new(FolderLevel.MonthLanguages.Slovenian, "Slovenian names"),
    ];

    private static readonly IReadOnlyList<EnumChoice<FolderLevel.DateFormats>> s_dateFormats =
    [
        new(FolderLevel.DateFormats.DayMonthYear, "02.11.2026"),
        new(FolderLevel.DateFormats.YearMonthDay, "2026-11-02"),
        new(FolderLevel.DateFormats.Compact, "20261102"),
    ];

    private static readonly IReadOnlyList<EnumChoice<FolderLevel.NameOrders>> s_nameOrders =
    [
        new(FolderLevel.NameOrders.DateFirst, "Date, then name"),
        new(FolderLevel.NameOrders.NameFirst, "Name, then date"),
    ];

    private readonly FolderStructureEditor _editor;
    private FolderLevel _level;

    internal FolderLevelViewModel(FolderStructureEditor editor, FolderLevel level)
    {
        _editor = editor;
        _level = level;
        MoveUpCommand = new RelayCommand(() => editor.Move(this, -1), () => editor.CanMove(this, -1));
        MoveDownCommand = new RelayCommand(() => editor.Move(this, 1), () => editor.CanMove(this, 1));
        RemoveCommand = new RelayCommand(() => editor.Remove(this), () => CanRemove);
    }

    public FolderLevel Level => _level;
    public FolderLevel.LevelKind Kind => _level.Kind;

    public string Title => Kind switch
    {
        FolderLevel.LevelKind.MediaType => "Photo / Video",
        FolderLevel.LevelKind.Month => "Month",
        FolderLevel.LevelKind.Text => "Folder",
        FolderLevel.LevelKind.Split => "Unedited / Exported",
        _ => "Project",
    };

    public bool IsMediaType => Kind == FolderLevel.LevelKind.MediaType;
    public bool IsMonth => Kind == FolderLevel.LevelKind.Month;
    public bool IsText => Kind == FolderLevel.LevelKind.Text;
    public bool IsSplit => Kind == FolderLevel.LevelKind.Split;
    public bool IsProject => Kind == FolderLevel.LevelKind.Project;

    /// <summary>Every structure has a project folder.</summary>
    public bool CanRemove => !IsProject;
    public string RemoveToolTip => CanRemove ? "Remove this level" : "Every structure has a project folder";

    public string Photo { get => _level.Photo ?? ""; set => Update(_level with { Photo = value }); }
    public string Video { get => _level.Video ?? ""; set => Update(_level with { Video = value }); }
    public string Text { get => _level.Text ?? ""; set => Update(_level with { Text = value }); }
    public string Unedited { get => _level.Unedited ?? ""; set => Update(_level with { Unedited = value }); }
    public string Exported { get => _level.Exported ?? ""; set => Update(_level with { Exported = value }); }

    public FolderLevel.MonthStyles MonthStyle
    {
        get => _level.MonthStyle ?? FolderLevel.MonthStyles.NumberAndName;
        set => Update(_level with { MonthStyle = value });
    }

    public FolderLevel.MonthLanguages MonthLanguage
    {
        get => _level.MonthLanguage ?? FolderLevel.MonthLanguages.English;
        set => Update(_level with { MonthLanguage = value });
    }

    public FolderLevel.DateFormats DateFormat
    {
        get => _level.DateFormat ?? FolderLevel.DateFormats.DayMonthYear;
        set => Update(_level with { DateFormat = value });
    }

    public FolderLevel.NameOrders NameOrder
    {
        get => _level.NameOrder ?? FolderLevel.NameOrders.DateFirst;
        set => Update(_level with { NameOrder = value });
    }

    // Instance properties, so the window's bindings can reach them.
    public IReadOnlyList<EnumChoice<FolderLevel.MonthStyles>> MonthStyleChoices => s_monthStyles;
    public IReadOnlyList<EnumChoice<FolderLevel.MonthLanguages>> MonthLanguageChoices => s_monthLanguages;
    public IReadOnlyList<EnumChoice<FolderLevel.DateFormats>> DateFormatChoices => s_dateFormats;
    public IReadOnlyList<EnumChoice<FolderLevel.NameOrders>> NameOrderChoices => s_nameOrders;

    public ICommand MoveUpCommand { get; }
    public ICommand MoveDownCommand { get; }
    public ICommand RemoveCommand { get; }

    internal void RefreshCommands()
    {
        ((RelayCommand)MoveUpCommand).RaiseCanExecuteChanged();
        ((RelayCommand)MoveDownCommand).RaiseCanExecuteChanged();
    }

    private void Update(FolderLevel level, [CallerMemberName] string? name = null)
    {
        if (level == _level) return;
        _level = level;
        OnPropertyChanged(name);
        _editor.Changed();
    }
}

/// <summary>A folder made in every video project.</summary>
public sealed class VideoFolderViewModel : ObservableObject
{
    private readonly FolderStructureEditor _editor;
    private string _name;

    internal VideoFolderViewModel(FolderStructureEditor editor, string name)
    {
        _editor = editor;
        _name = name;
        RemoveCommand = new RelayCommand(() => editor.Remove(this));
    }

    public string Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value)) _editor.Changed();
        }
    }

    public ICommand RemoveCommand { get; }
}

/// <summary>Where videos and audio go: a video folder, or the project folder itself (<see cref="Folder"/> is null).</summary>
public sealed class MediaChoice : ObservableObject
{
    public MediaChoice(VideoFolderViewModel? folder)
    {
        Folder = folder;
        if (folder is not null)
        {
            folder.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(VideoFolderViewModel.Name)) OnPropertyChanged(nameof(Title));
            };
        }
    }

    public VideoFolderViewModel? Folder { get; }

    public string Title => Folder switch
    {
        null => "The project folder",
        { Name.Length: 0 } => "Untitled folder",
        var folder => folder.Name,
    };
}
