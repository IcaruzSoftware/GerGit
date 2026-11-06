using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GerGit;

/// <summary>
/// Remembered values: one JSON file, loaded on first use, written atomically on
/// every change. Keys are dotted names (e.g. "lastUsed.fetch.allRemotes"); per-repository keys
/// come from <see cref="RepoKey"/>. Folder: $GERGIT_CONFIG_DIR (tests), else $XDG_CONFIG_HOME/gergit, else ~/.config/gergit; as root /root/.config/gergit.
/// Limitation: flat keys in one file (records such as `integration.terminal` are JSON objects under one key), not the
/// nested objects. The keys of the Repository Settings window are in <see cref="RepoSettings"/>
/// (&lt;git dir&gt;/gergit/settings.json); the older <see cref="RepoKey"/> keys are still here.
/// </summary>
public static class Settings
{
    static readonly object Lock = new();
    static JsonObject? s_data;

    public const string SpaceCharKey = "general.branchNameSpaceChar";

    /// <summary>The character a typed or pasted space becomes in reference names.</summary>
    public static string SpaceReplacement => Get(SpaceCharKey, "-");

    /// <summary>A remembered width or height: a hand-edited value that is not a positive finite number gives the default.</summary>
    public static double GetLength(string key, double defaultValue) =>
        Get(key, defaultValue) is var v && double.IsFinite(v) && v > 0 ? v : defaultValue;

    /// <summary>In <see cref="GerGit.Core.ConfigFolder.Location"/> (as root /root/.config/gergit).</summary>
    public static string FilePath => Path.Combine(GerGit.Core.ConfigFolder.Location, "settings.json");

    /// <summary>Per-repository key, e.g. RepoKey(tab.Path, "recent.remote").</summary>
    public static string RepoKey(string repoPath, string key) => $"repo:{repoPath}:{key}";

    public static T Get<T>(string key, T defaultValue)
    {
        lock (Lock)
        {
            if (Data.TryGetPropertyValue(key, out var node) && node is not null)
            {
                try { return node.Deserialize<T>() ?? defaultValue; }
                catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException) { }
            }
            return defaultValue;
        }
    }

    /// <summary>Stores the value (null removes the key) and writes the file.</summary>
    public static void Set<T>(string key, T value)
    {
        lock (Lock)
        {
            if (value is null) Data.Remove(key);
            else Data[key] = JsonSerializer.SerializeToNode(value);
            Save();
        }
    }

    /// <summary>Forgets the loaded data so the next access reads the file again (tests).</summary>
    internal static void Reload()
    {
        lock (Lock) s_data = null;
    }

    static JsonObject Data => s_data ??= Load();

    static JsonObject Load()
    {
        try
        {
            if (File.Exists(FilePath) && JsonNode.Parse(File.ReadAllText(FilePath)) is JsonObject o) return o;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"Cannot read {FilePath}: {e.Message}"); // a broken file starts over with the defaults
            if (e is JsonException)
            {
                try { File.Copy(FilePath, FilePath + ".bak", overwrite: true); } // kept for the user before the next write
                catch (Exception copy) when (copy is IOException or UnauthorizedAccessException) { }
            }
        }
        return [];
    }

    // Write a temp file in the same folder, then rename.
    static void Save()
    {
        var path = FilePath;
        try
        {
            GerGit.Core.ConfigFolder.CreateFolderOf(path); // 0700: the socket folder below it must be private
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Data.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Cannot write {path}: {e.Message}"); // the value stays in memory for this session
        }
    }
}
