using System.Collections.Concurrent;

namespace GerGit.Core;

/// <summary>
/// Absolute paths of external programs (git, setsid, env, xdg-open, notify-send, terminals, editors). Every process
/// GerGit starts gets an absolute path from here: for a bare name .NET's Process.Start looks in the app folder and the
/// current directory before PATH, so GerGit started inside a cloned repository (`gergit .`) would run an executable
/// named `git` or `setsid` from that repository. Only rooted PATH entries count (a relative entry depends on the
/// current directory too).
/// </summary>
public static class ProgramPath
{
    static readonly ConcurrentDictionary<string, string?> s_cache = new();

    /// <summary><see cref="Find"/> with the process PATH, cached for the process lifetime (a program installed later is
    /// found after a restart).</summary>
    public static string? Of(string name) => s_cache.GetOrAdd(name, n => Find(n, Environment.GetEnvironmentVariable("PATH")));

    /// <summary>A name with a `/` is taken as a path (made absolute; it must be executable); a bare name is searched in
    /// the rooted entries of <paramref name="pathVariable"/>. Null when not found or not executable.</summary>
    public static string? Find(string name, string? pathVariable)
    {
        if (name.Length == 0) return null;
        if (name.Contains('/')) return IsExecutable(name) ? Path.GetFullPath(name) : null;
        foreach (var dir in (pathVariable ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries).Where(Path.IsPathRooted))
        {
            var full = Path.Combine(dir, name);
            if (IsExecutable(full)) return full;
        }
        return null;
    }

    /// <summary>An existing file (not a folder) with an execute bit.</summary>
    public static bool IsExecutable(string path) =>
        File.Exists(path) && !OperatingSystem.IsWindows() &&
        (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
}
