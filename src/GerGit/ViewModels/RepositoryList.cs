using System.Text.Json.Serialization;
using GerGit.Core;

namespace GerGit.ViewModels;

/// <summary>One repository of the list: normalised path, optional display alias and
/// the last-opened time in seconds since the epoch (null = never).
/// Not built yet: a colour field (repository colours, tab ring, colour row).</summary>
public sealed record RepoEntry(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("alias")] string? Alias = null,
    [property: JsonPropertyName("lastOpened")] long? LastOpened = null);

/// <summary>
/// The Repository Manager's data, stored in
/// <see cref="Settings"/> next to <see cref="AppPreferences.SourceFoldersKey"/>. Read and written on the UI thread.
/// </summary>
public static class RepositoryList
{
    public const string EntriesKey = "repositories.repositories", IgnoredKey = "repositories.ignored",
        ScanDepthKey = "repositories.scanDepth";

    /// <summary>Resolves a path to the key git reports (<see cref="RepositoryScan.Normalize"/>); tests make it block.
    /// It touches the file system (a hung network mount blocks it), so lists of stored paths are only normalised in the
    /// background; on the UI thread only the few paths of one user action are.</summary>
    internal static Func<string, string> Normalizer { get; set; } = RepositoryScan.Normalize;

    /// <summary><see cref="Normalizer"/>, but a malformed stored path stays as it is.</summary>
    internal static string SafeNormalize(string path)
    {
        try { return Normalizer(path); }
        catch (ArgumentException) { return path; }
    }

    /// <summary>All entries as stored (no file-system access). Paths are <see cref="RepositoryScan.Normalize"/>d when
    /// written; older ones are fixed by <see cref="NormalizeStoredAsync"/>.</summary>
    public static List<RepoEntry> Entries
    {
        // A hand-edited entry without a path is left out (it would break lookups and the tree).
        get => Settings.Get<List<RepoEntry?>>(EntriesKey, []).OfType<RepoEntry>().Where(e => !string.IsNullOrEmpty(e.Path)).ToList();
        set => Settings.Set(EntriesKey, value);
    }

    static Task<bool>? s_normalizing;

    /// <summary><see cref="NormalizeStoredAsync"/> once per session (after the first session restore).</summary>
    public static Task<bool> NormalizeStoredOnceAsync() => s_normalizing ??= NormalizeStoredAsync();

    /// <summary>For tests: the next <see cref="NormalizeStoredOnceAsync"/> runs again.</summary>
    internal static void ResetNormalizeOnce() => s_normalizing = null;

    /// <summary>Stored paths written before links were resolved (or through a link that changed) get the resolved key;
    /// two entries for the same folder merge (the first alias, the newest last-opened time). Computed in the
    /// background; written only when the list did not change meanwhile, else the pass repeats (at most 3 passes). True
    /// when it was rewritten.</summary>
    public static async Task<bool> NormalizeStoredAsync()
    {
        for (int pass = 0; pass < 3; pass++)
        {
            var list = Entries;
            var merged = await Task.Run(() => list.Where(e => !string.IsNullOrEmpty(e.Path)).GroupBy(e => SafeNormalize(e.Path))
                .Select(g => new RepoEntry(g.Key, g.Select(e => e.Alias).FirstOrDefault(a => a is not null), g.Max(e => e.LastOpened)))
                .ToList());
            if (merged.SequenceEqual(list)) return false;
            if (!Entries.SequenceEqual(list)) continue; // changed meanwhile (an open, a scan): once more on the new list
            Entries = merged;
            return true;
        }
        return false;
    }

    /// <summary>Paths scans skip (added by Remove...).</summary>
    public static List<string> Ignored
    {
        get => Settings.Get<List<string>>(IgnoredKey, []);
        set => Settings.Set(IgnoredKey, value);
    }

    public static int ScanDepth => Settings.Get(ScanDepthKey, 5);

    /// <summary>The stored source folder list and its resolved form, swapped as one object (read by the scan's thread).</summary>
    sealed record SourceCache(string[] Raw, List<string> Resolved);

    static SourceCache? s_sources;

    /// <summary>The source folders, normalised (no trailing slash). Cached: resolved again on the calling thread only
    /// when the stored list changed (Preferences), else by <see cref="RefreshSourceFolders"/> in the scan's background
    /// work.</summary>
    public static List<string> SourceFolders
    {
        get
        {
            var raw = AppPreferences.SourceFolders.ToArray();
            return Volatile.Read(ref s_sources) is { } c && c.Raw.SequenceEqual(raw) ? c.Resolved : RefreshSourceFolders(raw);
        }
    }

    /// <summary>Resolves the source folders again (a link may point elsewhere now) and caches them.</summary>
    internal static List<string> RefreshSourceFolders(string[]? raw = null)
    {
        raw ??= AppPreferences.SourceFolders.ToArray();
        var resolved = raw.Select(SafeNormalize).Distinct().ToList();
        Volatile.Write(ref s_sources, new SourceCache(raw, resolved));
        return resolved;
    }

    public static RepoEntry? Find(string path)
    {
        path = Normalizer(path);
        return Entries.FirstOrDefault(e => e.Path == path);
    }

    /// <summary>The last-opened time of an ordinary repository becomes now.
    /// A repository that is not in the list yet (File > Open, a drop) is added, as the drop on a folder row
    /// "records it as opened".</summary>
    public static void RecordOpened(string path, DateTimeOffset now)
    {
        path = Normalizer(path);
        var list = Entries;
        int i = list.FindIndex(e => e.Path == path);
        var stamp = now.ToUnixTimeSeconds();
        if (i < 0) list.Add(new(path, LastOpened: stamp));
        else list[i] = list[i] with { LastOpened = stamp };
        Entries = list;
    }

    /// <summary>Adds the folders that are not in the list yet (a drop: a few paths of one user action); returns how many
    /// were added.</summary>
    public static int Add(IEnumerable<string> paths) => AddNormalized(paths.Select(Normalizer));

    static int AddNormalized(IEnumerable<string> paths)
    {
        var list = Entries;
        var known = list.Select(e => e.Path).ToHashSet(StringComparer.Ordinal);
        int added = 0;
        foreach (var p in paths)
            if (known.Add(p)) { list.Add(new(p)); added++; }
        if (added > 0) Entries = list;
        return added;
    }

    /// <summary>Scan results (the path as walked and its normalised form, computed in the background), checked against
    /// the list as it is now: paths below a source folder that was removed meanwhile, or ignored meanwhile (Remove...),
    /// are not added. Returns how many were added.</summary>
    public static int AddScanned(IEnumerable<(string Walked, string Real)> found) => AddNormalized(Current(found, ignore: true));

    /// <summary>Reset mode of the scan: the list becomes exactly the result (only below the current source folders);
    /// aliases and last-opened times are lost.</summary>
    public static void Replace(IEnumerable<(string Walked, string Real)> found) =>
        Entries = Current(found, ignore: false).Distinct().Select(p => new RepoEntry(p)).ToList();

    static IEnumerable<string> Current(IEnumerable<(string Walked, string Real)> found, bool ignore)
    {
        var sources = SourceFolders;
        var ignored = ignore ? Ignored : []; // stored normalised by Remove...; compared as stored, no file-system access
        return found.Where(f => sources.Any(s => IsBelow(f.Walked, s))
                                && !ignored.Any(i => IsBelow(f.Walked, i) || IsBelow(f.Real, i)))
            .Select(f => f.Real);
    }

    /// <summary>Remove... of repository rows: the entries go; with <paramref name="ignore"/> later scans skip their paths.</summary>
    public static void Remove(IReadOnlyCollection<string> paths, bool ignore)
    {
        Entries = Entries.Where(e => !paths.Contains(e.Path)).ToList();
        if (ignore) Ignored = Ignored.Union(paths).ToList();
    }

    /// <summary>Remove... of folder rows: each folder's path below its source folder is ignored below every source folder,
    /// and every entry in one of those folders goes.</summary>
    public static void RemoveFolders(IReadOnlyCollection<string> folders)
    {
        var sources = SourceFolders;
        var gone = folders.SelectMany(f => sources.Where(s => IsBelow(f, s)).MaxBy(s => s.Length) is { } own
                ? sources.Select(s => Path.Combine(s, Path.GetRelativePath(own, f)))
                : [f])
            .Concat(folders).Distinct().ToList();
        Entries = Entries.Where(e => !gone.Any(g => IsBelow(e.Path, g))).ToList();
        Ignored = Ignored.Union(gone).ToList();
    }

    /// <summary>Removing a source folder (Preferences) drops every entry below it, without
    /// ignoring them.</summary>
    public static void RemoveBelow(string folder)
    {
        folder = Normalizer(folder);
        var remaining = SourceFolders; // already without the removed folder
        var list = Entries;
        // An entry still below another source folder (nested source folders) keeps its alias and last-opened time.
        if (list.RemoveAll(e => IsBelow(e.Path, folder) && !remaining.Any(s => IsBelow(e.Path, s))) > 0) Entries = list;
    }

    /// <summary>Stores the display alias; null, blank or the automatic name (the folder name) clears it.</summary>
    public static void SetAlias(string path, string? alias)
    {
        path = Normalizer(path);
        alias = alias?.Trim();
        if (string.IsNullOrEmpty(alias) || alias == Path.GetFileName(path)) alias = null;
        var list = Entries;
        int i = list.FindIndex(e => e.Path == path);
        if (i < 0 || list[i].Alias == alias) return;
        list[i] = list[i] with { Alias = alias };
        Entries = list;
    }

    /// <summary>The path itself or a path below the folder.</summary>
    public static bool IsBelow(string path, string folder) =>
        path == folder || path.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);
}
