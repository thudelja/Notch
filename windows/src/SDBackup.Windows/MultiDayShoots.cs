using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json.Serialization;
using System.Windows.Input;
using SDBackup.Core;

namespace SDBackup.Windows;

/// <summary>
/// A shoot over several days or cards, e.g. a calendar: its folder ("AMD Koledar 2026") takes the project folder's place in
/// the folder structures, and each day's project goes inside it.
/// </summary>
public sealed record MultiDayShoot
{
    [JsonPropertyName("finished")] public bool Finished { get; init; }
    [JsonPropertyName("id")] public Guid Id { get; init; } = Guid.NewGuid();
    [JsonPropertyName("lastUsed")] public DateTime LastUsed { get; init; } = DateTime.UtcNow;
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("started")] public DateTime Started { get; init; } = DateTime.UtcNow;

    /// <summary>Offered in the import panel: not finished, and used in the last 100 days.</summary>
    [JsonIgnore] public bool IsOpen => !Finished && LastUsed > DateTime.UtcNow - MultiDayShootStore.HiddenAfter;
}

/// <summary>
/// The multi-day shoots, saved in series.json next to the project list. Local to this PC: each project records its shoot's
/// name, so its folders don't depend on this list.
/// </summary>
public sealed class MultiDayShootStore
{
    /// <summary>Shoots unused for this long leave the import panel.</summary>
    public static readonly TimeSpan HiddenAfter = TimeSpan.FromDays(100);

    private readonly string? _file;
    private readonly List<MultiDayShoot> _all;

    /// <param name="file">Null keeps them in memory only (used by the self-test).</param>
    public MultiDayShootStore(string? file)
    {
        _file = file;
        _all = (file is null ? null : Json.ReadFile<List<MultiDayShoot>>(file)) ?? [];
    }

    public IReadOnlyList<MultiDayShoot> All => _all;

    /// <summary>Shoots to offer in the import panel, most recently used first.</summary>
    public IReadOnlyList<MultiDayShoot> Open => _all.Where(s => s.IsOpen).OrderByDescending(s => s.LastUsed).ToList();

    /// <summary>Raised after any change.</summary>
    public event Action? Changed;

    public MultiDayShoot? Shoot(Guid id) => _all.FirstOrDefault(s => s.Id == id);

    /// <summary>The shoot with this name (reopened if it was finished), or a new one.</summary>
    public MultiDayShoot Start(string name)
    {
        var trimmed = name.Trim();
        var index = _all.FindIndex(s => string.Equals(s.Name, trimmed, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            _all[index] = _all[index] with { Finished = false, LastUsed = Now };
        }
        else
        {
            _all.Add(new MultiDayShoot { Name = trimmed, Started = Now, LastUsed = Now });
            index = _all.Count - 1;
        }
        Save();
        return _all[index];
    }

    public void MarkUsed(string name) =>
        Update(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase), s => s with { LastUsed = Now });

    public void Finish(Guid id) => Update(s => s.Id == id, s => s with { Finished = true });

    /// <summary>Back in the import panel, as if just used.</summary>
    public void Reopen(Guid id) => Update(s => s.Id == id, s => s with { Finished = false, LastUsed = Now });

    private void Update(Func<MultiDayShoot, bool> match, Func<MultiDayShoot, MultiDayShoot> change)
    {
        var index = _all.FindIndex(s => match(s));
        if (index < 0) return;
        _all[index] = change(_all[index]);
        Save();
    }

    /// <summary>Whole seconds, as stored.</summary>
    private static DateTime Now
    {
        get
        {
            var now = DateTime.UtcNow;
            return new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        }
    }

    private void Save()
    {
        if (_file is not null)
        {
            try { Json.WriteFile(_file, _all); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
        Changed?.Invoke();
    }
}

/// <summary>A multi-day shoot in Settings, with its status and buttons.</summary>
public sealed class ShootViewModel(MultiDayShoot shoot, ICommand finish, ICommand reopen)
{
    public string Name => shoot.Name;
    public bool IsOpen => shoot.IsOpen;
    public bool IsClosed => !shoot.IsOpen;

    public string Status
    {
        get
        {
            var used = shoot.LastUsed.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture);
            if (shoot.Finished) return $"Finished · last used {used}";
            return shoot.IsOpen ? $"Open · last used {used}" : $"Not used for 100 days · last used {used}";
        }
    }

    public string FinishTitle => "Hold to finish";
    public string FinishAccessibleName => $"Finish “{shoot.Name}”";
    public string ReopenAccessibleName => $"Reopen “{shoot.Name}”";

    /// <summary>The hold-to-finish button's command.</summary>
    public ICommand FinishCommand { get; } = finish;
    public ICommand ReopenCommand { get; } = reopen;
}
