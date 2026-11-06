using System.Diagnostics;
using System.Runtime.Versioning;

namespace GerGit.Core;

/// <summary>GerGit's own settings folder (also of Settings and AccountStore): $GERGIT_CONFIG_DIR (tests), else
/// $XDG_CONFIG_HOME/gergit, else ~/.config/gergit; as root always /root/.config/gergit (sudo keeps the user's HOME and
/// XDG variables, and a root-owned 0700 folder in the user's home would lock the user out). It is private (0700): the
/// single-instance and askpass sockets may live in its `run` folder, which must not sit in a folder others can
/// write.</summary>
public static class ConfigFolder
{
    const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    public const string RootLocation = "/root/.config/gergit";

    public static string Location =>
        LocationFor(Environment.GetEnvironmentVariable("GERGIT_CONFIG_DIR"), Environment.IsPrivilegedProcess);

    internal static string LocationFor(string? isolated, bool privileged)
    {
        if (isolated is { Length: > 0 }) return isolated;
        if (privileged) return RootLocation;
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrEmpty(xdg)) xdg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Path.Combine(xdg, "gergit");
    }

    /// <summary>Creates the folder of <paramref name="filePath"/>. When that is the settings folder: with mode 0700, see
    /// <see cref="Create"/>. Any other folder (a store with its own path, tests) is created plainly.</summary>
    public static void CreateFolderOf(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath)!;
        if (OperatingSystem.IsWindows() || Path.TrimEndingDirectorySeparator(dir) != Path.TrimEndingDirectorySeparator(Location))
            Directory.CreateDirectory(dir);
        else Create(dir);
    }

    /// <summary>Creates <paramref name="dir"/> (GerGit's own settings folder) with mode 0700; an existing one that group
    /// or others can write is tightened to 0700 (logged). A symlink is left as it is (its target may be shared; the run
    /// folder check then decides). Never touches a parent folder. Returns the folder.</summary>
    [UnsupportedOSPlatform("windows")]
    public static string Create(string dir)
    {
        Directory.CreateDirectory(dir, Private);
        if (new DirectoryInfo(dir).LinkTarget is not null) // dotfiles or a shared folder: never chmod its target
        {
            Trace.WriteLine($"'{dir}' is a symlink: left as it is");
            return dir;
        }
        try
        {
            var mode = File.GetUnixFileMode(dir);
            if ((mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            {
                File.SetUnixFileMode(dir, Private);
                Trace.WriteLine($"'{dir}' was writable by group or others ({mode}); made it private (0700)");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) // not ours: the run folder check refuses it
        {
            Trace.WriteLine($"Cannot make '{dir}' private: {e.Message}");
        }
        return dir;
    }
}
