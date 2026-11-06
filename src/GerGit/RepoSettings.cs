using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using GerGit.Core;

namespace GerGit;

/// <summary>
/// Per-repository settings: `&lt;common git dir&gt;/gergit/settings.json` (GerGit decision: shared by all
/// worktrees of the repository), flat camelCase keys, unknown keys
/// kept, read on each access (a small file) and written atomically without following a planted `gergit` link.
/// Limitation: only the keys of the Repository Settings window live here; `recent.remote`, `recent.pushRefspec` and
/// `recent.branchPrefix` still use <see cref="Settings.RepoKey"/> in the app file (move them when this file grows).
/// </summary>
public static class RepoSettings
{
    public static string FilePath(string commonDir) => Path.Combine(commonDir, "gergit", "settings.json");

    public static T Get<T>(string commonDir, string key, T defaultValue)
    {
        if (Load(commonDir).TryGetPropertyValue(key, out var node) && node is not null)
        {
            try { return node.Deserialize<T>() ?? defaultValue; }
            catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException) { }
        }
        return defaultValue;
    }

    /// <summary>Stores the value (null removes the key) and writes the file; a failure is logged (the value is lost).</summary>
    public static void Set<T>(string commonDir, string key, T value)
    {
        var data = Load(commonDir);
        if (value is null) data.Remove(key);
        else data[key] = JsonSerializer.SerializeToNode(value);
        var path = FilePath(commonDir);
        try { SafeFile.WriteAllText(path, data.ToJsonString(new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Cannot write {path}: {e.Message}");
        }
    }

    static JsonObject Load(string commonDir)
    {
        var path = FilePath(commonDir);
        try
        {
            // A regular file only (no FIFO or link that could block or point elsewhere), at most 1 MB.
            if (RepositoryScan.IsRegularFile(path) && new FileInfo(path).Length <= 1 << 20
                && JsonNode.Parse(File.ReadAllText(path)) is JsonObject o) return o;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"Cannot read {path}: {e.Message}"); // the defaults apply
        }
        return [];
    }
}

/// <summary>
/// The two switches of Enable switches and the links of a commit message: issue tracker links only when the
/// global preference `integration.issueLinks` and the repository's `issueLinks` are both on (default on); plain http(s)
/// URLs always.
/// </summary>
public static class IssueLinks
{
    public const string GlobalKey = "integration.issueLinks", RepoKey = "issueLinks";

    public static bool GlobalEnabled
    {
        get => Settings.Get(GlobalKey, true);
        set
        {
            Settings.Set(GlobalKey, value);
            RaiseChanged();
        }
    }

    public static bool RepoEnabled(RepoInfo info) => RepoSettings.Get(info.CommonDir, RepoKey, true);

    /// <summary>Rules beyond this many are not used (logged): compiling is outside the per-text time budget.</summary>
    public const int MaxRules = 100;

    /// <summary>Raised on the UI thread when a switch or the rules changed: shown messages are linked again.</summary>
    public static event Action? Changed;

    public static void RaiseChanged() => Changed?.Invoke();

    /// <summary>The usable rules of the repository (none when a switch is off); patterns that do not compile are
    /// skipped and logged. Reads files: call off the UI thread.</summary>
    public static List<(System.Text.RegularExpressions.Regex Regex, string Url)> LoadRules(RepoInfo info, CancellationToken ct = default)
    {
        if (!GlobalEnabled || !RepoEnabled(info)) return [];
        var all = IssueTracker.Read(info);
        if (all.Count > MaxRules) System.Diagnostics.Trace.WriteLine($"Issue tracker rules: only the first {MaxRules} of {all.Count} are used");
        var rules = new List<(System.Text.RegularExpressions.Regex, string)>();
        foreach (var r in all.Take(MaxRules))
        {
            ct.ThrowIfCancellationRequested();
            if (IssueTracker.Compile(r.Regex) is { } regex) rules.Add((regex, r.Url));
        }
        return rules;
    }

    /// <summary>The links of each text, computed on a pool thread (file reads and untrusted patterns); a cancelled
    /// request stops between matches and throws <see cref="OperationCanceledException"/>.</summary>
    public static Task<List<TextLink>[]> FindAsync(RepoInfo info, CancellationToken ct, params string[] texts) => Task.Run(() =>
    {
        var rules = LoadRules(info, ct);
        return texts.Select(t => IssueTracker.Find(t, rules, plainUrls: true, ct)).ToArray();
    }, ct);
}
