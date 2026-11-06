using System.ComponentModel;
using System.Diagnostics;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.ViewModels;

/// <summary>An external program found on PATH: display name and absolute path.</summary>
public sealed record ExternalApp(string Name, string Path);

/// <summary>
/// Open in menu and the Terminal button: terminal and editor detection on Linux and
/// starting them in the repository folder. Programs are started with an argument list (never a shell string) and the
/// repository folder as working directory.
/// </summary>
public static class OpenIn
{
    /// <summary>Environment lookup (PATH, TERMINAL); tests point it at a folder of stub programs.</summary>
    internal static Func<string, string?> Env { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>
    /// Terminal auto-detection order and the arguments that open it in a folder
    /// ("{0}" = the folder). Every terminal also gets the folder as its working directory, which is all
    /// x-terminal-emulator (a Debian alternative with varying targets) and xterm get.
    /// Entries beyond wezterm, ptyxis, kgx, tilix, terminator, mate-terminal and lxterminal, and the
    /// folder argument of each terminal, are best-effort.
    /// </summary>
    internal static readonly (string Exe, string[] DirArgs)[] Terminals =
    [
        ("x-terminal-emulator", []),
        ("gnome-terminal", ["--working-directory={0}"]),
        ("konsole", ["--workdir", "{0}"]),
        ("xfce4-terminal", ["--working-directory={0}"]),
        ("kitty", ["--directory", "{0}"]),
        ("alacritty", ["--working-directory", "{0}"]),
        ("wezterm", ["start", "--cwd", "{0}"]),
        ("foot", ["--working-directory={0}"]),
        ("ptyxis", ["--new-window", "--working-directory={0}"]),
        ("kgx", ["--working-directory={0}"]),
        ("tilix", ["--working-directory={0}"]),
        ("terminator", ["--working-directory={0}"]),
        ("mate-terminal", ["--working-directory={0}"]),
        ("lxterminal", ["--working-directory={0}"]),
        ("xterm", []),
    ];

    /// <summary>Code editors opened with the repository folder (a common list plus
    /// VSCodium). The set and the executable names are best-effort.</summary>
    internal static readonly (string Name, string[] Exes)[] Editors =
    [
        ("Antigravity", ["antigravity"]),
        ("CodeBuddy", ["codebuddy"]),
        ("Cursor", ["cursor"]),
        ("Fleet", ["fleet"]),
        ("Visual Studio Code", ["code"]),
        ("Visual Studio Code Insiders", ["code-insiders"]),
        ("VSCodium", ["codium"]),
        ("Sublime Text", ["subl"]),
        ("Atom", ["atom"]),
        ("WebStorm", ["webstorm"]),
        ("Zed", ["zed", "zeditor"]),
    ];

    /// <summary>JetBrains IDEs offered for a repository with an `.idea` folder (JetBrains Toolbox script names, then
    /// snap names). The set is best-effort.</summary>
    internal static readonly (string Name, string[] Exes)[] IdeaIdes =
    [
        ("IntelliJ IDEA", ["idea", "intellij-idea-ultimate", "intellij-idea-community"]),
        ("PyCharm", ["pycharm", "pycharm-professional", "pycharm-community"]),
        ("GoLand", ["goland"]),
        ("PhpStorm", ["phpstorm"]),
        ("CLion", ["clion"]),
        ("RubyMine", ["rubymine"]),
        ("RustRover", ["rustrover"]),
    ];

    /// <summary><see cref="ProgramPath.Find"/> with <see cref="Env"/>'s PATH (not cached: the menu detects each time).</summary>
    public static string? FindOnPath(string name) => ProgramPath.Find(name, Env("PATH"));

    static ExternalApp? Find(string name, string[] exes) =>
        exes.Select(FindOnPath).FirstOrDefault(p => p is not null) is { } path ? new(name, path) : null;

    /// <summary>The terminal of "Default": $TERMINAL (name or path), else the first of <see cref="Terminals"/> on PATH.
    /// Null when none is found.</summary>
    public static ExternalApp? DetectTerminal()
    {
        if (Env("TERMINAL") is { Length: > 0 } variable && FindOnPath(variable.Trim()) is { } path)
            return new(Path.GetFileName(path), path);
        return Terminals.Select(t => Find(t.Exe, [t.Exe])).FirstOrDefault(t => t is not null);
    }

    /// <summary>Executable and arguments for the terminal setting: "auto" adds the folder argument of a known terminal;
    /// "custom" passes its own arguments and gets the folder only as working directory (unverified).
    /// Exe null = no terminal detected.</summary>
    public static (string? Exe, List<string> Args) TerminalCommand(TerminalSetting setting, string folder)
    {
        // A bare name in the setting is resolved on PATH, never in the current directory.
        if (setting.IsCustom) return (FindOnPath(setting.Path) ?? setting.Path, SplitArgs(setting.Args));
        if (DetectTerminal() is not { } found) return (null, []);
        var dirArgs = Terminals.FirstOrDefault(t => t.Exe == Path.GetFileName(found.Path)).DirArgs ?? [];
        return (found.Path, dirArgs.Select(a => string.Format(a, folder)).ToList());
    }

    /// <summary>Splits an argument line on whitespace; double quotes group (and are removed).</summary>
    public static List<string> SplitArgs(string line)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; any = true; }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any) result.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else { current.Append(c); any = true; }
        }
        if (any) result.Add(current.ToString());
        return result;
    }

    /// <summary>Start through util-linux setsid when it is on PATH: the program gets its own session, so closing the
    /// terminal GerGit was started from does not end it. setsid execs the program in place (same process).
    /// A file without an execute bit is refused before the start.
    /// Limitation: through setsid a file that has the bit but cannot be executed (ENOEXEC) fails inside setsid, which only
    /// prints the error, so no error dialog shows; tests switch setsid off to check the OS error text.</summary>
    internal static bool UseSetsid { get; set; } = GitRunner.HasSetsid;

    /// <summary>Starts the program (an absolute path) detached; the error text on failure, else null.</summary>
    internal static string? Start(string exe, IEnumerable<string> args, string workDir)
    {
        bool setsid = UseSetsid && GitRunner.SetsidPath is not null;
        if (setsid && !ProgramPath.IsExecutable(exe)) return $"'{exe}' is not an executable file.";
        try
        {
            var psi = new ProcessStartInfo(setsid ? GitRunner.SetsidPath! : exe) { WorkingDirectory = workDir, UseShellExecute = false };
            if (setsid)
            {
                psi.ArgumentList.Add("--");
                psi.ArgumentList.Add(exe);
            }
            foreach (var a in args) psi.ArgumentList.Add(a);
            Process.Start(psi)?.Dispose();
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException)
        {
            return e.Message;
        }
    }

    // The error dialogs have a text but no title of their own.
    const string ErrorTitle = "Error";
    internal const string NoTerminal = "Cannot find a terminal. Choose one in Preferences > Integration.";

    /// <summary>"Open In &lt;terminal name&gt;", the Terminal button and Ctrl+Alt+T.</summary>
    public static Task TerminalAsync(RepoTabViewModel tab)
    {
        var (exe, args) = TerminalCommand(AppPreferences.Current.Terminal, tab.Path);
        // Nothing detected: "Cannot find the terminal at" names a path, so this case has its own text.
        if (exe is null) return tab.ShowError(ErrorTitle, NoTerminal);
        if (!Path.IsPathRooted(exe) || !File.Exists(exe)) return tab.ShowError(ErrorTitle, $"Cannot find the terminal at '{exe}'");
        return Start(exe, args, tab.Path) is { } error ? tab.ShowError(ErrorTitle, error) : Task.CompletedTask;
    }

    /// <summary>"Open In File Explorer" and Ctrl+Alt+O: the desktop's file manager (xdg-open).</summary>
    public static Task FileExplorerAsync(RepoTabViewModel tab) => tab.Hosting.OpenPath(tab.Path);

    /// <summary>Starts an editor with <paramref name="target"/> as its only argument.</summary>
    public static Task EditorAsync(RepoTabViewModel tab, ExternalApp app, string target)
    {
        if (!File.Exists(app.Path)) return tab.ShowError(ErrorTitle, $"Cannot find {app.Name} at '{app.Path}'");
        return Start(app.Path, [target], tab.Path) is { } error
            ? tab.ShowError(ErrorTitle, $"Cannot run '{app.Path} {target}'.\n{error}")
            : Task.CompletedTask;
    }

    /// <summary>
    /// Open in menu, built when it opens: terminal, file manager; the project-file IDEs (a JetBrains IDE for
    /// an `.idea` folder, Rider per solution file in the repository root) and the code editors found on PATH; "View on
    /// &lt;Service&gt;" per remote with an http(s) repository page.
    /// Not built yet: solution files only in the repository root, no Visual Studio, no service icons on the View items and no
    /// custom commands.
    /// </summary>
    public static List<MenuEntry> Menu(RepoTabViewModel tab)
    {
        var root = tab.Path;
        var projects = new List<MenuEntry>();
        if (Directory.Exists(Path.Combine(root, ".idea")))
            foreach (var ide in IdeaIdes.Select(i => Find(i.Name, i.Exes)).OfType<ExternalApp>())
                projects.Add(Menus.Item($"Open '.idea' in {ide.Name}", () => EditorAsync(tab, ide, root)));
        if (Find("Rider", ["rider"]) is { } rider)
            foreach (var sln in Directory.EnumerateFiles(root).Where(f => f.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
                         || f.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
                projects.Add(Menus.Item($"Open '{Path.GetFileName(sln)}' in {rider.Name}", () => EditorAsync(tab, rider, sln)));
        var editors = Editors.Select(e => Find(e.Name, e.Exes)).OfType<ExternalApp>()
            .Select(e => Menus.Item($"Open in {e.Name}", () => EditorAsync(tab, e, root)));

        var hosted = tab.RemoteOps.RemoteNames.Select(tab.Hosted).OfType<HostedRemote>()
            .Where(h => h.ServiceName is not null && h.RepositoryPage is not null).ToList(); // only http(s) pages (RemoteUrl)
        var viewOn = hosted.Select(h => Menus.Item(hosted.Count == 1 ? $"View on {h.ServiceName}" : $"View {h.Name} on {h.ServiceName}",
            () => { tab.Hosting.OpenBrowser(h.RepositoryPage!); return Task.CompletedTask; }));

        return Menus.Join(
            [
                Menus.Item($"Open In {AppPreferences.Current.TerminalName}", () => TerminalAsync(tab), "Ctrl+Alt+T"),
                Menus.Item("Open In File Explorer", () => FileExplorerAsync(tab), "Ctrl+Alt+O"),
            ],
            projects.Concat(editors),
            viewOn);
    }

}
