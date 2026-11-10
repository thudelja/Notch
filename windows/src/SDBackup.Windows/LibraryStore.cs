using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using SDBackup.Core;

namespace SDBackup.Windows;

/// <summary>
/// Projects, the projects folders ("libraries") they live in, camera names and per-card import records.
/// Portable data lives in each projects folder (.sdbackup\library.json); data local to this PC lives in
/// %APPDATA%\Notch (PORTING.md §7).
/// </summary>
public sealed class LibraryStore
{
    private sealed record ProjectCache
    {
        [JsonPropertyName("formatVersion")] public int FormatVersion { get; init; } = 2;
        [JsonPropertyName("projects")] public List<ProjectInfo> Projects { get; init; } = [];
    }

    private sealed record CameraNameStore
    {
        /// <summary>Camera model → folder name, e.g. "Canon EOS R5" → "R5".</summary>
        public Dictionary<string, string> Models { get; init; } = new();
        /// <summary>Card ID → folder name for files without camera information.</summary>
        public Dictionary<string, string> Cards { get; init; } = new();
    }

    private readonly string? _folder;

    /// <summary>Multi-day shoots, local to this PC.</summary>
    public MultiDayShootStore Shoots { get; }
    private readonly List<ProjectInfo> _projects = [];
    private readonly Dictionary<Guid, LibraryLocation> _locations = new();
    private CameraNameStore _cameras = new();

    /// <param name="folder">Null keeps everything in memory (used by the self-test).</param>
    public LibraryStore(string? folder)
    {
        _folder = folder;
        Shoots = new MultiDayShootStore(folder is null ? null : Path.Combine(folder, "series.json"));
        if (folder is null) return;
        Directory.CreateDirectory(ImportsFolder!);
        foreach (var location in Json.ReadFile<List<LibraryLocation>>(LibrariesFile!) ?? [])
        {
            _locations.TryAdd(location.LibraryId, location);
        }
        _projects.AddRange(Json.ReadFile<ProjectCache>(ProjectsFile!)?.Projects ?? []);
        _cameras = Json.ReadFile<CameraNameStore>(CamerasFile!) ?? new CameraNameStore();
        RefreshLocations();
    }

    public IReadOnlyList<ProjectInfo> Projects => _projects;

    // ------------------------------------------------------------------ Projects folders

    /// <summary>
    /// The ID of the projects folder at <paramref name="root"/>, creating its manifest if needed. Null when the
    /// folder can't be created or written (e.g. its drive isn't connected).
    /// </summary>
    public Guid? Library(string root)
    {
        root = PathUtil.Normalize(root);
        if (!Directory.Exists(root))
        {
            // Only create the folder when its parent exists, so an unplugged drive isn't recreated.
            var parent = Path.GetDirectoryName(root);
            if (parent is null || !Directory.Exists(parent)) return null;
            try { Directory.CreateDirectory(root); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }

        var manifest = LibraryManifest.Read(root);
        if (manifest is null)
        {
            var knownId = _locations.Values.FirstOrDefault(l => PathUtil.AreSame(l.LastKnownPath, root))?.LibraryId ?? Guid.NewGuid();
            manifest = LibraryManifest.Create(knownId, _projects.Where(p => p.LibraryId == knownId));
            try { manifest.Write(root); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
        }

        _locations[manifest.LibraryId] = LibraryLocator.Describe(root, manifest.LibraryId);
        SaveLocations();
        Merge(manifest, root);
        SetRoot(manifest.LibraryId, root);
        return manifest.LibraryId;
    }

    /// <summary>
    /// Finds every known projects folder again (drives may have new letters, or be disconnected) and fills in
    /// each project's RootPath ("" when offline). Also picks up projects folders on newly connected drives, so
    /// an SSD prepared on a Mac shows its projects here.
    /// </summary>
    public void RefreshLocations()
    {
        if (_folder is null) return;
        var drives = LibraryLocator.ConnectedDriveRoots();
        DiscoverLibraries(drives);

        var roots = new Dictionary<Guid, string>();
        var registryChanged = false;
        foreach (var (id, location) in _locations.ToList())
        {
            var found = LibraryLocator.Resolve(location, drives);
            if (found is null) continue;
            roots[id] = found;
            var current = LibraryLocator.Describe(found, id);
            if (current != location)
            {
                _locations[id] = current;
                registryChanged = true;
            }
        }
        if (registryChanged) SaveLocations();

        foreach (var (id, root) in roots)
        {
            if (LibraryManifest.Read(root) is { } manifest) Merge(manifest, root);
            WriteManifest(id, root);
        }
        for (var i = 0; i < _projects.Count; i++)
        {
            var project = _projects[i];
            _projects[i] = project with
            {
                RootPath = roots.GetValueOrDefault(project.LibraryId) ?? "",
                VideoRootPath = project.VideoLibraryId is Guid videos ? roots.GetValueOrDefault(videos) ?? "" : "",
            };
        }
    }

    /// <summary>Registers projects folders found at the root, or one level down, of other drives.</summary>
    private void DiscoverLibraries(IEnumerable<string> drives)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "";
        var changed = false;
        foreach (var drive in drives)
        {
            if (string.Equals(drive, systemRoot, StringComparison.OrdinalIgnoreCase)) continue;
            IEnumerable<string> candidates;
            try
            {
                candidates = new[] { drive }.Concat(Directory.EnumerateDirectories(drive).Take(200)).ToList();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { continue; }

            foreach (var candidate in candidates)
            {
                if (!File.Exists(LibraryManifest.PathIn(candidate))) continue;
                if (LibraryManifest.Read(candidate) is not { } manifest || _locations.ContainsKey(manifest.LibraryId)) continue;
                _locations[manifest.LibraryId] = LibraryLocator.Describe(candidate, manifest.LibraryId);
                changed = true;
            }
        }
        if (changed) SaveLocations();
    }

    // ------------------------------------------------------------------ Folder structure

    /// <summary>The folder structure new projects' photos or videos in the projects folder at <paramref name="root"/> get.</summary>
    public FolderStructure Structure(FolderStructure.Branch branch, string root)
    {
        var manifest = LibraryManifest.Read(PathUtil.Normalize(root));
        return branch == FolderStructure.Branch.Photo
            ? manifest?.Layout ?? FolderStructure.Standard
            : manifest?.VideoLayout ?? FolderStructure.Standard;
    }

    /// <summary>
    /// Saves the folder structure new projects' photos or videos in the projects folder at <paramref name="root"/> get.
    /// Existing projects keep theirs. False when the folder isn't available.
    /// </summary>
    public bool SetStructure(FolderStructure structure, FolderStructure.Branch branch, string root)
    {
        root = PathUtil.Normalize(root);
        if (!structure.IsValid || Library(root) is not Guid libraryId) return false;
        var manifest = LibraryManifest.Read(root) ?? LibraryManifest.Create(libraryId, _projects.Where(p => p.LibraryId == libraryId));
        manifest = branch == FolderStructure.Branch.Photo
            // A folder set up before 1.3 has one structure for both: the videos keep it.
            ? manifest with { Structure = structure, VideoStructure = manifest.VideoStructure ?? manifest.Layout }
            : manifest with { VideoStructure = structure };
        try
        {
            manifest.Write(root);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // ------------------------------------------------------------------ Projects

    public IReadOnlyList<ProjectInfo> RecentProjects(int limit = 2) =>
        _projects.OrderByDescending(p => p.LastUsed).Take(limit).ToList();

    public ProjectInfo? Project(Guid id) => _projects.FirstOrDefault(p => p.Id == id);

    /// <summary>Where one kind of a new project's files goes: a projects folder laid out by its structure, or a folder the user chose (the project folder is made inside it).</summary>
    public sealed record Place(string Root, string? ChosenFolder = null);

    /// <summary>
    /// The existing project with this name and start day in these places, or a new one. Photos and videos each follow their
    /// projects folder's structure, without the Photo / Video level when they go to different folders; a project of a
    /// multi-day shoot (<paramref name="series"/>) goes in the shoot's folder. Null when a folder isn't available and was
    /// never seen on this PC.
    /// </summary>
    public ProjectInfo? NewProject(string name, ShootDay startDay, string? series, Place photos, Place videos)
    {
        var trimmed = name.Trim();
        var photoStructure = Structure(FolderStructure.Branch.Photo, photos.Root);
        var videoStructure = Structure(FolderStructure.Branch.Video, videos.Root);
        if (!PathUtil.AreSame(photos.Root, videos.Root))
        {
            photoStructure = photoStructure.WithoutMediaType;
            videoStructure = videoStructure.WithoutMediaType;
        }
        if (Resolve(photos, photoStructure.ProjectFolderNameFor(trimmed, startDay)) is not { } photo
            || Resolve(videos, videoStructure.ProjectFolderNameFor(trimmed, startDay)) is not { } video)
        {
            return null;
        }
        series = string.IsNullOrWhiteSpace(series) ? null : series.Trim();
        var existing = _projects.FirstOrDefault(p =>
            p.LibraryId == photo.Id && (p.Folder ?? []).SequenceEqual(photo.Folder ?? [], PathUtil.Comparer) && p.StartDay == startDay
            && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase)
            && string.Equals(p.Series ?? "", series ?? "", StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing with { RootPath = photo.Path };
        return new ProjectInfo
        {
            Name = trimmed, StartDay = startDay, LibraryId = photo.Id, Structure = photoStructure, Folder = photo.Folder,
            VideoLibraryId = video.Id, VideoStructure = videoStructure, VideoFolder = video.Folder, Series = series,
            RootPath = photo.Path, VideoRootPath = video.Path,
        };
    }

    /// <summary>
    /// The projects folder for a place, and the project folder's path in it for a chosen folder. A chosen folder belongs to
    /// the nearest projects folder at or above it (so its projects move with their year and are found on the Mac); one
    /// outside every projects folder becomes one.
    /// </summary>
    private (Guid Id, string Path, IReadOnlyList<string>? Folder)? Resolve(Place place, string projectFolderName)
    {
        if (place.ChosenFolder is { } chosenFolder)
        {
            var chosen = PathUtil.Normalize(chosenFolder);
            var root = ProjectsFolderContaining(chosen) ?? (Library(chosen) is null ? null : chosen);
            if (root is null || Library(root) is not Guid id) return null;
            var relative = Path.GetRelativePath(root, chosen);
            string[] below = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            return (id, root, [.. below, projectFolderName]);
        }
        var path = PathUtil.Normalize(place.Root);
        if (Library(path) is Guid libraryId) return (libraryId, path, null);
        // A folder on a drive that isn't connected is still recorded; its files wait for it.
        return _locations.Values.FirstOrDefault(l => PathUtil.AreSame(l.LastKnownPath, path)) is { } known ? (known.LibraryId, "", null) : null;
    }

    /// <summary>The known project whose folder is <paramref name="folder"/> (its photo or video project folder, or the folder it was created in).</summary>
    public ProjectInfo? ProjectAtFolder(string folder) =>
        _projects.FirstOrDefault(p => p.RootPath.Length > 0 && ProjectLayout.ProjectFolders(p).Any(f => PathUtil.AreSame(f, folder)));

    /// <summary>The nearest folder at or above <paramref name="folder"/> that is a projects folder (has a library.json).</summary>
    private static string? ProjectsFolderContaining(string folder)
    {
        for (var current = folder; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (LibraryManifest.Read(current) is not null) return current;
        }
        return null;
    }

    /// <summary>Saves new projects and moves these to the top of the recent list.</summary>
    public void MarkUsed(IEnumerable<ProjectInfo> used)
    {
        var now = DateTime.UtcNow;
        now = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc); // Whole seconds, as stored.
        var list = used.ToList();
        foreach (var project in list)
        {
            var updated = project with { LastUsed = now };
            var index = _projects.FindIndex(p => p.Id == project.Id);
            if (index >= 0) _projects[index] = updated;
            else _projects.Add(updated);
        }
        SaveCache();
        foreach (var project in list.Where(p => p.RootPath.Length > 0)) WriteManifest(project.LibraryId, project.RootPath);
        foreach (var project in list.Where(p => p.HasSeparateVideoFolder && p.VideoRootPath.Length > 0)) WriteManifest(project.VideoLibrary, project.VideoRootPath);
    }

    /// <summary>
    /// After a year folder was moved from <paramref name="oldRoot"/> to <paramref name="newRoot"/>: the projects (or, for
    /// projects with a separate videos folder, the videos) in it now belong to the projects folder at <paramref name="newRoot"/>.
    /// </summary>
    public int MoveProjects(int year, string oldRoot, string newRoot)
    {
        var oldId = LibraryManifest.Read(oldRoot)?.LibraryId
            ?? _locations.Values.FirstOrDefault(l => PathUtil.AreSame(l.LastKnownPath, oldRoot))?.LibraryId;
        if (oldId is null || Library(newRoot) is not Guid newId) return 0;

        var yearText = year.ToString("0000", System.Globalization.CultureInfo.InvariantCulture);
        var newPath = PathUtil.Normalize(newRoot);
        var changed = new List<ProjectInfo>();
        for (var i = 0; i < _projects.Count; i++)
        {
            var project = _projects[i];
            var moved = false;
            if (project.LibraryId == oldId && ProjectLayout.RelativePhotoFolder(project).FirstOrDefault() == yearText)
            {
                project = project with { LibraryId = newId, RootPath = newPath };
                moved = true;
            }
            if (project.HasSeparateVideoFolder && project.VideoLibraryId == oldId && ProjectLayout.RelativeVideoFolder(project).FirstOrDefault() == yearText)
            {
                project = project with { VideoLibraryId = newId, VideoRootPath = newPath };
                moved = true;
            }
            if (!moved) continue;
            if (project.VideoLibraryId is null) project = project with { VideoRootPath = "" };
            _projects[i] = project;
            changed.Add(project);
        }
        if (changed.Count > 0)
        {
            SaveCache();
            WriteManifest(oldId.Value, oldRoot);
            WriteManifest(newId, newRoot);
            // The other folder each project lives in lists it too.
            foreach (var project in changed)
            {
                if (project.RootPath.Length > 0 && project.LibraryId != newId) WriteManifest(project.LibraryId, project.RootPath);
                if (project.HasSeparateVideoFolder && project.VideoRootPath.Length > 0 && project.VideoLibrary != newId)
                {
                    WriteManifest(project.VideoLibrary, project.VideoRootPath);
                }
            }
        }
        return changed.Count;
    }

    // ------------------------------------------------------------------ Camera names

    public string CameraName(string model, string? make) =>
        _cameras.Models.GetValueOrDefault(model) ?? CameraNames.ShortName(make, model);

    public string? CameraNameForCard(string cardId) => _cameras.Cards.GetValueOrDefault(cardId);

    /// <summary>Key "" holds the name for files without camera information, remembered for this card.</summary>
    public void RememberCameraNames(IReadOnlyDictionary<string, string> names, string cardId)
    {
        foreach (var (key, name) in names)
        {
            var trimmed = name.Trim();
            if (trimmed.Length == 0) continue;
            if (key.Length == 0) _cameras.Cards[cardId] = trimmed;
            else _cameras.Models[key] = trimmed;
        }
        Save(CamerasFile, _cameras);
    }

    public void ForgetCameraNames()
    {
        _cameras = new CameraNameStore();
        Save(CamerasFile, _cameras);
    }

    // ------------------------------------------------------------------ Import records

    public ImportLedger Ledger(string cardId) =>
        _folder is null ? new ImportLedger() : Json.ReadFile<ImportLedger>(LedgerFile(cardId)) ?? new ImportLedger();

    public void SaveLedger(ImportLedger ledger, string cardId)
    {
        if (_folder is not null) Save(LedgerFile(cardId), ledger);
    }

    /// <summary>After this, every file on every card is offered again.</summary>
    public void ForgetImports()
    {
        if (ImportsFolder is null) return;
        try
        {
            Directory.Delete(ImportsFolder, recursive: true);
            Directory.CreateDirectory(ImportsFolder);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------------ Keeping things in sync

    /// <summary>Adds projects found in a projects folder's manifest (e.g. created on another computer).</summary>
    private void Merge(LibraryManifest manifest, string root)
    {
        var changed = false;
        foreach (var project in manifest.Projects)
        {
            var known = _projects.FirstOrDefault(p => p.Id == project.Id);
            // A videos folder lists the projects whose videos it holds; they belong to another folder.
            var incoming = project.HasSeparateVideoFolder && project.VideoLibraryId == manifest.LibraryId
                ? project with { VideoRootPath = root, RootPath = known?.RootPath ?? "" }
                : project with { LibraryId = manifest.LibraryId, RootPath = root, VideoRootPath = known?.VideoRootPath ?? "" };
            var index = _projects.FindIndex(p => p.Id == incoming.Id);
            if (index < 0)
            {
                _projects.Add(incoming);
                changed = true;
            }
            else if (incoming.LastUsed > _projects[index].LastUsed)
            {
                _projects[index] = incoming;
                changed = true;
            }
        }
        if (changed) SaveCache();
    }

    private void SetRoot(Guid libraryId, string root)
    {
        for (var i = 0; i < _projects.Count; i++)
        {
            if (_projects[i].LibraryId == libraryId) _projects[i] = _projects[i] with { RootPath = root };
            if (_projects[i].VideoLibraryId == libraryId) _projects[i] = _projects[i] with { VideoRootPath = root };
        }
    }

    /// <summary>
    /// Rewrites the folder's manifest only if it would change, so connecting a drive doesn't rewrite it needlessly. The
    /// folder's structure is kept.
    /// </summary>
    private void WriteManifest(Guid libraryId, string root)
    {
        var existing = LibraryManifest.Read(root);
        var updated = LibraryManifest.Create(libraryId,
            _projects.Where(p => p.LibraryId == libraryId || (p.HasSeparateVideoFolder && p.VideoLibraryId == libraryId)),
            existing?.Structure, existing?.VideoStructure);
        if (existing is not null && (existing.LibraryId != libraryId || existing.Serialized() == updated.Serialized())) return;
        try { updated.Write(root); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void SaveCache() => Save(ProjectsFile, new ProjectCache { Projects = _projects.Select(p => p.Portable).ToList() });

    private void SaveLocations() => Save(LibrariesFile, _locations.Values.OrderBy(l => l.LastKnownPath, StringComparer.Ordinal).ToList());

    private static void Save<T>(string? path, T value)
    {
        if (path is null) return;
        try { Json.WriteFile(path, value); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private string? ProjectsFile => _folder is null ? null : Path.Combine(_folder, "projects.json");
    private string? LibrariesFile => _folder is null ? null : Path.Combine(_folder, "libraries.json");
    private string? CamerasFile => _folder is null ? null : Path.Combine(_folder, "cameras.json");
    private string? ImportsFolder => _folder is null ? null : Path.Combine(_folder, "Imports");
    private string LedgerFile(string cardId) => Path.Combine(ImportsFolder!, NameRules.SanitizedFolderName(cardId, "card") + ".json");
}
