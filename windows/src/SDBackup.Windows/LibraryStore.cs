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
    private readonly List<ProjectInfo> _projects = [];
    private readonly Dictionary<Guid, LibraryLocation> _locations = new();
    private CameraNameStore _cameras = new();

    /// <param name="folder">Null keeps everything in memory (used by the self-test).</param>
    public LibraryStore(string? folder)
    {
        _folder = folder;
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

    /// <summary>The folder structure new projects in the projects folder at <paramref name="root"/> get.</summary>
    public FolderStructure Structure(string root) =>
        LibraryManifest.Read(PathUtil.Normalize(root))?.Layout ?? FolderStructure.Standard;

    /// <summary>
    /// Saves the folder structure new projects in the projects folder at <paramref name="root"/> get. Existing projects
    /// keep theirs. False when the folder isn't available.
    /// </summary>
    public bool SetStructure(FolderStructure structure, string root)
    {
        root = PathUtil.Normalize(root);
        if (!structure.IsValid || Library(root) is not Guid libraryId) return false;
        var manifest = LibraryManifest.Read(root) ?? LibraryManifest.Create(libraryId, _projects.Where(p => p.LibraryId == libraryId));
        try
        {
            (manifest with { Structure = structure }).Write(root);
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

    /// <summary>
    /// The existing project with this name and start day in the folder at <paramref name="root"/>, or a new one there,
    /// with its videos in <paramref name="videosRoot"/> when that is a separate folder. Null when a folder isn't available.
    /// </summary>
    public ProjectInfo? ProjectNamed(string name, ShootDay startDay, string root, string? videosRoot = null)
    {
        if (Library(root) is not Guid libraryId) return null;
        var trimmed = name.Trim();
        var rootPath = PathUtil.Normalize(root);
        var existing = _projects.FirstOrDefault(p =>
            p.LibraryId == libraryId && p.Folder is null && p.StartDay == startDay && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing with { RootPath = rootPath };

        Guid? videoLibraryId = null;
        var videoRootPath = "";
        if (videosRoot is not null && !PathUtil.AreSame(videosRoot, rootPath))
        {
            // A videos folder on a drive that isn't connected is still recorded; its videos wait for it.
            var path = PathUtil.Normalize(videosRoot);
            videoLibraryId = Library(path) ?? _locations.Values.FirstOrDefault(l => PathUtil.AreSame(l.LastKnownPath, path))?.LibraryId;
            if (videoLibraryId is null) return null;
            videoRootPath = Directory.Exists(path) ? path : "";
        }
        // A new project follows the folder's current structure, and keeps it from then on.
        var structure = LibraryManifest.Read(rootPath)?.Structure;
        return new ProjectInfo
        {
            Name = trimmed, StartDay = startDay, LibraryId = libraryId, RootPath = rootPath, Structure = structure,
            VideoLibraryId = videoLibraryId, VideoRootPath = videoRootPath,
        };
    }

    /// <summary>
    /// A project in <paramref name="parent"/>, a folder the user chose: its folder is <c>parent\&lt;date and name&gt;</c>,
    /// named by <paramref name="structure"/>, and both photos and videos go into it. The project belongs to the nearest
    /// projects folder at or above <paramref name="parent"/> (so it moves with its year and is found again on the Mac);
    /// a folder outside every projects folder becomes one. Null when it can't be written.
    /// </summary>
    public ProjectInfo? ProjectNamed(string name, ShootDay startDay, string parent, FolderStructure structure)
    {
        parent = PathUtil.Normalize(parent);
        var root = ProjectsFolderContaining(parent) ?? (Library(parent) is null ? null : parent);
        if (root is null || Library(root) is not Guid libraryId) return null;
        var trimmed = name.Trim();
        var relative = Path.GetRelativePath(root, parent);
        string[] below = relative == "." ? [] : relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        IReadOnlyList<string> folder = [.. below, structure.ProjectFolderNameFor(trimmed, startDay)];
        var existing = _projects.FirstOrDefault(p => p.LibraryId == libraryId && p.Folder is { } f && f.SequenceEqual(folder, PathUtil.Comparer));
        if (existing is not null) return existing with { RootPath = root };
        return new ProjectInfo { Name = trimmed, StartDay = startDay, LibraryId = libraryId, RootPath = root, Structure = structure, Folder = folder };
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
            _projects.Where(p => p.LibraryId == libraryId || (p.HasSeparateVideoFolder && p.VideoLibraryId == libraryId)), existing?.Structure);
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
