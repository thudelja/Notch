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
            _projects[i] = _projects[i] with { RootPath = roots.GetValueOrDefault(_projects[i].LibraryId) ?? "" };
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

    // ------------------------------------------------------------------ Projects

    public IReadOnlyList<ProjectInfo> RecentProjects(int limit = 3) =>
        _projects.OrderByDescending(p => p.LastUsed).Take(limit).ToList();

    public ProjectInfo? Project(Guid id) => _projects.FirstOrDefault(p => p.Id == id);

    /// <summary>The existing project with this name and start day in the folder at <paramref name="root"/>, or a new one there.</summary>
    public ProjectInfo? ProjectNamed(string name, ShootDay startDay, string root)
    {
        if (Library(root) is not Guid libraryId) return null;
        var trimmed = name.Trim();
        var rootPath = PathUtil.Normalize(root);
        var existing = _projects.FirstOrDefault(p =>
            p.LibraryId == libraryId && p.StartDay == startDay && string.Equals(p.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        return existing is not null
            ? existing with { RootPath = rootPath }
            : new ProjectInfo { Name = trimmed, StartDay = startDay, LibraryId = libraryId, RootPath = rootPath };
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
    }

    /// <summary>After a year was moved: its projects now belong to the projects folder at <paramref name="newRoot"/>.</summary>
    public int MoveProjects(int year, string oldRoot, string newRoot)
    {
        var oldId = LibraryManifest.Read(oldRoot)?.LibraryId
            ?? _locations.Values.FirstOrDefault(l => PathUtil.AreSame(l.LastKnownPath, oldRoot))?.LibraryId;
        if (oldId is null || Library(newRoot) is not Guid newId) return 0;

        var changed = 0;
        for (var i = 0; i < _projects.Count; i++)
        {
            if (_projects[i].LibraryId != oldId || _projects[i].StartDay.Year != year) continue;
            _projects[i] = _projects[i] with { LibraryId = newId, RootPath = PathUtil.Normalize(newRoot) };
            changed++;
        }
        if (changed > 0)
        {
            SaveCache();
            WriteManifest(oldId.Value, oldRoot);
            WriteManifest(newId, newRoot);
        }
        return changed;
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
            var incoming = project with { LibraryId = manifest.LibraryId, RootPath = root };
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
        }
    }

    /// <summary>Rewrites the folder's manifest only if it would change, so connecting a drive doesn't rewrite it needlessly.</summary>
    private void WriteManifest(Guid libraryId, string root)
    {
        var updated = LibraryManifest.Create(libraryId, _projects.Where(p => p.LibraryId == libraryId));
        var existing = LibraryManifest.Read(root);
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
