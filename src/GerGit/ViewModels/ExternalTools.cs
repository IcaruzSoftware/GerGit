using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.ViewModels;

public enum ToolKind { Diff, Merge }

/// <summary>
/// One stored external tool: Id = the predefined tool's display name, or "custom".
/// Name only for custom tools; Path and Args only when set (predefined: overridden); Primary only when true, Hidden only
/// when the tool is not visible.
/// </summary>
public sealed record ToolSetting(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Name = null,
    [property: JsonPropertyName("path"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Path = null,
    [property: JsonPropertyName("args"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Args = null,
    [property: JsonPropertyName("primary"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Primary = false,
    [property: JsonPropertyName("hidden"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Hidden = false)
{
    public const string CustomId = "custom";
}

/// <summary>
/// An external diff or merge tool: a predefined one with its built-in
/// candidates and default arguments, or a custom one. Edited in place by the Preferences page; menus load fresh copies.
/// </summary>
public sealed partial class ExternalTool : ObservableObject
{
    public ExternalTool(ToolKind kind, string name, string defaultArgs, IReadOnlyList<string> candidates, bool isCustom)
    {
        Kind = kind;
        Name = name;
        DefaultArgs = defaultArgs;
        Candidates = candidates;
        IsCustom = isCustom;
    }

    public ToolKind Kind { get; }
    public bool IsCustom { get; }
    public bool IsPredefined => !IsCustom;
    public string DefaultArgs { get; }

    /// <summary>Built-in candidates: bare program names (searched on PATH) or absolute paths.</summary>
    public IReadOnlyList<string> Candidates { get; }

    [ObservableProperty] public partial string Name { get; set; }

    /// <summary>The configured path: custom tools always, predefined only when overridden. Several candidates are
    /// separated by `;`.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownPath), nameof(IsAvailable))]
    public partial string? PathOverride { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Args))]
    public partial string? ArgsOverride { get; set; }

    [ObservableProperty] public partial bool IsPrimary { get; set; }
    [ObservableProperty] public partial bool IsVisible { get; set; } = true;

    /// <summary>The argument template; editing a predefined tool's arguments back to the default clears the override.</summary>
    public string Args
    {
        get => ArgsOverride ?? DefaultArgs;
        set => ArgsOverride = IsCustom || value != DefaultArgs ? value : null;
    }

    bool _searched;
    string? _searchedPath;

    /// <summary>The first built-in candidate found (searched once per instance).</summary>
    public string? SearchedPath
    {
        get
        {
            if (!_searched) (_searchedPath, _searched) = (Candidates.Select(ExternalTools.FindBuiltIn).FirstOrDefault(p => p is not null), true);
            return _searchedPath;
        }
    }

    /// <summary>Preferences `Path:` box: the configured path, else the searched one, else empty.</summary>
    public string ShownPath => PathOverride is { Length: > 0 } p ? p : SearchedPath ?? "";

    /// <summary>A tool is available when its path is non-empty (configured, or a candidate was found).</summary>
    public bool IsAvailable => ShownPath.Length > 0;

    /// <summary>The absolute executable to start: the first configured candidate that exists, else the searched path.
    /// Null = missing (the "Cannot find" error).</summary>
    public string? Exe => PathOverride is { Length: > 0 } p
        ? p.Split(';').Select(c => ExternalTools.Locate(c)).FirstOrDefault(x => x is not null)
        : SearchedPath;

    /// <summary>`Reset to default` (predefined): searched path, default arguments, visible.</summary>
    public void ResetToDefault()
    {
        PathOverride = null;
        ArgsOverride = null;
        IsVisible = true;
    }

    /// <summary>The stored record; null = nothing to store (an untouched predefined tool, an incomplete custom one).</summary>
    public ToolSetting? ToSetting()
    {
        if (IsCustom)
            return Name.Trim().Length > 0 && PathOverride is { Length: > 0 } && Args.Trim().Length > 0
                ? new(ToolSetting.CustomId, Name, PathOverride, Args, IsPrimary, !IsVisible) : null;
        var s = new ToolSetting(Name, null, PathOverride is { Length: > 0 } p ? p : null, ArgsOverride, IsPrimary, !IsVisible);
        return s == new ToolSetting(Name) ? null : s;
    }
}

/// <summary>One side of an external diff: Rev null = the working-tree file, <see cref="GitRepository.IndexRev"/> (`:0`)
/// = the index, else a revision (`&lt;sha&gt;`, `&lt;sha&gt;~`, `HEAD`). Missing = the file does not exist on that side (empty temp file).</summary>
public sealed record DiffSide(string? Rev, string Path, bool Missing = false);

/// <summary>
/// External diff and merge tools: the
/// predefined Linux tools, the stored overrides and custom tools, the menus, and running a tool.
/// Security: a tool is started from an absolute path with an argument vector, never through a shell. The template is
/// split into arguments first and each variable is then replaced inside its one argument, in a single pass, so a file
/// name can never add arguments or shell syntax, nor bring in another variable. Temp file names are built from a
/// sanitised file name inside a private (0700) folder per run. Tool commands come only from GerGit's settings,
/// never from repository files.
/// </summary>
public static partial class ExternalTools
{
    public const string DiffToolsKey = "integration.diffTools", MergeToolsKey = "integration.mergeTools";
    public const string DiffHint = "Available variables: $LOCAL, $REMOTE", MergeHint = "Available variables: $LOCAL, $REMOTE, $BASE, $MERGED";

    /// <summary>
    /// "Predefined diff tools" with a Linux program to look for; Meld is included as well. Left out: Araxis Merge, Visual Studio and WinMerge (no Linux program) and vimdiff (needs a terminal).
    /// The flatpak candidates are best-effort.
    /// </summary>
    static readonly (string Name, string Args, string[] Candidates)[] DiffDefs =
    [
        ("Beyond Compare", "\"$REMOTE\" \"$LOCAL\"", ["bcompare"]),
        ("CodeBuddy", "--diff --wait \"$REMOTE\" \"$LOCAL\"", ["codebuddy"]),
        ("Cursor", "--diff --wait \"$REMOTE\" \"$LOCAL\"", ["cursor"]),
        ("KDiff3", "\"$REMOTE\" \"$LOCAL\"", ["kdiff3", .. Flatpak("org.kde.kdiff3")]),
        ("Meld", "\"$REMOTE\" \"$LOCAL\"", ["meld", .. Flatpak("org.gnome.meld")]),
        ("P4Merge", "\"$REMOTE\" \"$LOCAL\"", ["p4merge"]),
        ("VS Code", "--diff --wait \"$REMOTE\" \"$LOCAL\"", ["code", .. Flatpak("com.visualstudio.code")]),
    ];

    /// <summary>"Predefined merge tools" (same selection as <see cref="DiffDefs"/>, plus YAMLMerge).</summary>
    static readonly (string Name, string Args, string[] Candidates)[] MergeDefs =
    [
        ("Beyond Compare", "\"$REMOTE\" \"$LOCAL\" \"$BASE\" \"$MERGED\"", ["bcompare"]),
        ("CodeBuddy", "-n --wait --merge \"$REMOTE\" \"$LOCAL\" \"$BASE\" \"$MERGED\"", ["codebuddy"]),
        ("Cursor", "-n --wait --merge \"$REMOTE\" \"$LOCAL\" \"$BASE\" \"$MERGED\"", ["cursor"]),
        ("KDiff3", "\"$REMOTE\" -b \"$BASE\" \"$LOCAL\" -o \"$MERGED\"", ["kdiff3", .. Flatpak("org.kde.kdiff3")]),
        ("Meld", "\"$LOCAL\" \"$BASE\" \"$REMOTE\" --output \"$MERGED\"", ["meld", .. Flatpak("org.gnome.meld")]),
        ("P4Merge", "\"$BASE\" \"$REMOTE\" \"$LOCAL\" \"$MERGED\"", ["p4merge"]),
        ("VS Code", "-n --wait --merge \"$REMOTE\" \"$LOCAL\" \"$BASE\" \"$MERGED\"", ["code", .. Flatpak("com.visualstudio.code")]),
        ("YAMLMerge", "merge -p \"$BASE\" \"$REMOTE\" \"$LOCAL\" \"$MERGED\"", ["UnityYAMLMerge"]),
    ];

    static string[] Flatpak(string id) =>
        ["/var/lib/flatpak/exports/bin/" + id, System.IO.Path.Combine(Home, ".local/share/flatpak/exports/bin", id)];

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Finds a built-in candidate; tests replace it (the developer's own tools must not show up).</summary>
    internal static Func<string, string?> FindBuiltIn { get; set; } = Locate;

    /// <summary>A candidate → absolute executable or null: `~/` is the home folder, an absolute path must be an
    /// executable file, a bare name is searched in PATH's rooted entries. A relative path with a `/` is refused (it
    /// would depend on the current directory).</summary>
    public static string? Locate(string candidate)
    {
        var c = candidate.Trim();
        if (c.StartsWith("~/", StringComparison.Ordinal)) c = System.IO.Path.Combine(Home, c[2..]);
        if (c.Length == 0 || (c.Contains('/') && !System.IO.Path.IsPathRooted(c))) return null;
        return OpenIn.FindOnPath(c);
    }

    static string Key(ToolKind kind) => kind == ToolKind.Diff ? DiffToolsKey : MergeToolsKey;
    public static string Hint(ToolKind kind) => kind == ToolKind.Diff ? DiffHint : MergeHint;

    /// <summary>Every tool of the kind: the predefined ones with their stored overrides, then the custom ones.</summary>
    public static List<ExternalTool> Load(ToolKind kind)
    {
        var stored = Settings.Get<ToolSetting[]>(Key(kind), []).Where(s => s is not null).ToList();
        var tools = new List<ExternalTool>();
        foreach (var (name, args, candidates) in kind == ToolKind.Diff ? DiffDefs : MergeDefs)
        {
            var t = new ExternalTool(kind, name, args, candidates, isCustom: false);
            if (stored.FirstOrDefault(s => s.Id == name) is { } s) Apply(t, s);
            tools.Add(t);
        }
        foreach (var s in stored.Where(s => s.Id == ToolSetting.CustomId && s.Name is { Length: > 0 }))
        {
            var t = new ExternalTool(kind, s.Name!, "", [], isCustom: true);
            Apply(t, s);
            tools.Add(t);
        }
        return tools;
    }

    static void Apply(ExternalTool t, ToolSetting s)
    {
        t.PathOverride = s.Path;
        t.ArgsOverride = s.Args;
        t.IsPrimary = s.Primary;
        t.IsVisible = !s.Hidden;
    }

    public static void Save(ToolKind kind, IEnumerable<ExternalTool> tools) =>
        Settings.Set(Key(kind), tools.Select(t => t.ToSetting()).OfType<ToolSetting>().ToArray());

    /// <summary>The menu list: available and visible tools, alphabetical by name.</summary>
    public static List<ExternalTool> Visible(ToolKind kind) =>
        Load(kind).Where(t => t.IsVisible && t.IsAvailable).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The tool flagged primary, else the first (Ctrl+D, the conflict card button).</summary>
    public static ExternalTool? Primary(IReadOnlyList<ExternalTool> tools) => tools.FirstOrDefault(t => t.IsPrimary) ?? tools.FirstOrDefault();

    // ------------------------------------------------------------------ arguments

    [GeneratedRegex(@"\$(LOCAL|REMOTE|BASE|MERGED)")]
    private static partial Regex Variable();

    /// <summary>The argument vector: the template is split into arguments (whitespace; double quotes group and are
    /// removed), then every `$LOCAL` / `$REMOTE` / `$BASE` / `$MERGED` inside an argument is replaced in one pass. A
    /// variable the kind does not have stays as written.</summary>
    public static List<string> BuildArgs(string template, IReadOnlyDictionary<string, string> values) =>
        OpenIn.SplitArgs(template)
            .Select(a => Variable().Replace(a, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value))
            .ToList();

    // ------------------------------------------------------------------ temp files

    static readonly ConcurrentBag<string> s_folders = [];
    static int s_exitHooked;

    const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Where the run folders live: `$XDG_CACHE_HOME/gergit/tools` (default `~/.cache/gergit/tools`), not /tmp:
    /// flatpak tools (Meld, KDiff3, VS Code) get their own /tmp but see the home folder. Tests point it elsewhere.</summary>
    internal static string ToolsRoot { get; set; } = DefaultToolsRoot();

    internal static string DefaultToolsRoot()
    {
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        return System.IO.Path.Combine(cache is { Length: > 0 } && System.IO.Path.IsPathRooted(cache) ? cache : System.IO.Path.Combine(Home, ".cache"),
            "gergit", "tools");
    }

    /// <summary>The random part of a run folder name; tests replace it to hit taken names.</summary>
    internal static Func<string> RandomName { get; set; } = () => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));

    static string? s_swept; // the tools root already swept for leftovers in this process

    [DllImport("libc", SetLastError = true)]
    static extern int mkdir([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);

    const int EEXIST = 17;

    /// <summary>A fresh private folder for one run: `&lt;tools root&gt;/gergit-&lt;8 hex of the repository path's SHA-1&gt;-&lt;random&gt;`.
    /// `gergit/` and `gergit/tools/` are created 0700 (a wider one is tightened) and refused when they are symbolic
    /// links. The run folder is made by mkdir(2) with mode 0700, which fails on any existing entry (folder, file or
    /// link): then another name is tried. Afterwards it must be a real folder with mode 0700. The first run per
    /// process (and root) removes leftover run folders older than a day (a crash skips the cleanup at exit). Every
    /// folder of this process is removed when GerGit exits.</summary>
    internal static string NewRunFolder(string repoPath)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (Interlocked.Exchange(ref s_exitHooked, 1) == 0) AppDomain.CurrentDomain.ProcessExit += (_, _) => CleanUp();
        var root = ToolsRoot;
        foreach (var dir in (string[])[System.IO.Path.GetDirectoryName(root)!, root])
        {
            Directory.CreateDirectory(dir, Private);
            if (new DirectoryInfo(dir).LinkTarget is not null) throw new IOException($"'{dir}' is a symbolic link.");
            if (File.GetUnixFileMode(dir) != Private) File.SetUnixFileMode(dir, Private); // an older, wider folder
        }
        if (Interlocked.Exchange(ref s_swept, root) != root) SweepLeftovers(root);
        var hash = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(repoPath)))[..8];
        while (true)
        {
            var folder = System.IO.Path.Combine(root, $"gergit-{hash}-{RandomName()}");
            if (mkdir(folder, 0b111_000_000) != 0)
            {
                int errno = Marshal.GetLastPInvokeError();
                if (errno == EEXIST) continue;
                throw new IOException($"Cannot create '{folder}' (errno {errno}).");
            }
            var info = new DirectoryInfo(folder);
            if (info.LinkTarget is not null) throw new IOException($"'{folder}' is a symbolic link.");
            if (File.GetUnixFileMode(folder) != Private) File.SetUnixFileMode(folder, Private); // a umask that took user bits
            if (File.GetUnixFileMode(folder) != Private) throw new IOException($"'{folder}' is not private.");
            s_folders.Add(folder);
            return folder;
        }
    }

    /// <summary>Removes `gergit-*` run folders in <paramref name="root"/> older than a day (left by a crash). Only real
    /// folders: links and files are left alone, and the delete never follows a link inside.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    static void SweepLeftovers(string root)
    {
        var limit = DateTime.UtcNow.AddDays(-1);
        foreach (var info in new DirectoryInfo(root).EnumerateDirectories("gergit-*"))
        {
            try
            {
                if (info.LinkTarget is not null || info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LastWriteTimeUtc >= limit) continue;
                info.Delete(recursive: true); // unlinks links inside, never their targets
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.WriteLine(e.Message); }
        }
    }

    /// <summary>Removes every temp folder of this process.</summary>
    internal static void CleanUp()
    {
        while (s_folders.TryTake(out var folder))
        {
            try { Directory.Delete(folder, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Trace.WriteLine(e.Message); }
        }
    }

    /// <summary>Only letters, digits and `. _ - ~ ^`; anything else becomes `_`.</summary>
    static string Safe(string s, int max)
    {
        var sb = new StringBuilder(Math.Min(s.Length, max));
        foreach (var c in s.Take(max)) sb.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or '~' or '^' ? c : '_');
        return sb.ToString();
    }

    /// <summary>`&lt;file name without extension&gt;~&lt;label&gt;&lt;extension&gt;` from the last path segment, sanitised (the
    /// extension is kept for syntax highlighting). Never a path: it cannot leave the run folder.</summary>
    internal static string TempName(string repoPath, string label)
    {
        var name = System.IO.Path.GetFileName(repoPath.TrimEnd('/'));
        var ext = System.IO.Path.GetExtension(name);
        if (ext.Length > 16) ext = "";
        var stem = Safe(System.IO.Path.GetFileNameWithoutExtension(name), 64);
        return $"{(stem.Length > 0 ? stem : "file")}~{Safe(label, 32)}{Safe(ext, 16)}";
    }

    /// <summary>Writes the bytes to a new file (an existing file or link is never followed or overwritten).</summary>
    static async Task<string> WriteNewAsync(string folder, string name, byte[] bytes)
    {
        var path = System.IO.Path.Combine(folder, name);
        await using var f = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await f.WriteAsync(bytes);
        return path;
    }

    static string Label(string? rev) => rev switch
    {
        null => "Working",
        GitRepository.IndexRev => "Index",
        _ => ShaPrefix().Replace(rev, m => m.Value[..7]),
    };

    [GeneratedRegex("^[0-9a-f]{40,64}")]
    private static partial Regex ShaPrefix();

    /// <summary>The working-tree file of <paramref name="path"/>. Refused (IOException) when it lies outside the work
    /// tree or any folder between the work-tree root and the file is a symbolic link (the file itself may be one).</summary>
    internal static string WorkingFile(string repoPath, string path)
    {
        var root = System.IO.Path.GetFullPath(repoPath).TrimEnd('/') + "/";
        var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path));
        if (!full.StartsWith(root, StringComparison.Ordinal)) throw new IOException($"'{path}' is outside the repository.");
        for (var dir = System.IO.Path.GetDirectoryName(full); dir is not null && dir.Length >= root.Length; dir = System.IO.Path.GetDirectoryName(dir))
            if (new DirectoryInfo(dir).LinkTarget is not null) throw new IOException($"'{path}' is below a symbolic link.");
        return full;
    }

    /// <summary>A side as a file for the tool: a revision or the index → a temp file with git's bytes (`cat-file
    /// --filters`); the working tree → the file itself, a symbolic link → a temp file with its target text (as git
    /// shows it), a missing file → an empty temp file.</summary>
    static async Task<string> SideFileAsync(RepoTabViewModel tab, string folder, DiffSide side, CancellationToken ct, string suffix = "")
    {
        var name = TempName(side.Path, Label(side.Rev) + suffix);
        if (side.Missing) return await WriteNewAsync(folder, name, []);
        if (side.Rev is not null) return await WriteNewAsync(folder, name, await tab.Repo.ReadFileAtAsync(side.Rev, side.Path, ct));
        var full = WorkingFile(tab.Path, side.Path);
        var info = new FileInfo(full);
        if (info.LinkTarget is { } target) return await WriteNewAsync(folder, name, Encoding.UTF8.GetBytes(target));
        return info.Exists ? full : await WriteNewAsync(folder, name, []);
    }

    // ------------------------------------------------------------------ running

    // The title of these error dialogs is the same as in OpenIn.
    const string ErrorTitle = "Error";

    /// <summary>External diff (job `External diff`): two temp files, then the tool with `$REMOTE` = left
    /// and `$LOCAL` = right. Not waited for. Errors: `Cannot find external diff tool at '&lt;path&gt;'`, git's error,
    /// or `Cannot run '&lt;path&gt;'.` + the reason. <paramref name="showError"/> = the error window over the calling
    /// window (default: the tab's).</summary>
    public static Task DiffAsync(RepoTabViewModel tab, ExternalTool tool, DiffSide left, DiffSide right,
        Func<string, string, Task>? showError = null) =>
        RunAsync(tab, tool, "External diff", "external diff tool", showError, async (folder, ct) => new Dictionary<string, string>
        {
            ["REMOTE"] = await SideFileAsync(tab, folder, left, ct),
            // Both sides with the same name (same revision): the right one gets "-2".
            ["LOCAL"] = await SideFileAsync(tab, folder, right, ct,
                TempName(left.Path, Label(left.Rev)) == TempName(right.Path, Label(right.Rev)) ? "-2" : ""),
        });

    /// <summary>
    /// "External merge tools", procedure: ours (`$LOCAL`), theirs (`$REMOTE`) and base (`$BASE`) as
    /// `&lt;name&gt;~Local|Remote|Base&lt;ext&gt;` temp files, `$MERGED` = the working-tree file. The tool is started and not
    /// waited for; GerGit checks nothing afterwards and never stages the file: the file watcher sees the saved file,
    /// the conflict card turns "resolved" and the user presses Stage.
    /// The stages are read with `git cat-file --filters :&lt;n&gt;:&lt;path&gt;` straight into the temp
    /// folder (no `checkout-index --temp` file in the work tree); a stage the index does not have (base of a both-added
    /// file, ours or theirs of AU / UA) gives an empty file instead of an empty argument, which most tools refuse.
    /// </summary>
    public static Task MergeAsync(RepoTabViewModel tab, ExternalTool tool, string path) =>
        RunAsync(tab, tool, "External Merge", "external merge tool", null, async (folder, ct) =>
        {
            var merged = WorkingFile(tab.Path, path);
            // The tool writes $MERGED: never through a link that a merged branch put there.
            if (new FileInfo(merged).LinkTarget is not null) throw new IOException($"'{path}' is a symbolic link.");
            // A stage the index does not have (no common ancestor; AU / UA: added on one side only) is an empty file;
            // any other read error is shown.
            var present = await tab.Repo.ReadStagesPresentAsync(path, ct);
            if (!present.Any(x => x)) throw new GitException($"'{path}' is not unmerged.");
            async Task<string> StageFileAsync(int stage, string label) => await WriteNewAsync(folder, TempName(path, label),
                present[stage - 1] ? await tab.Repo.ReadFileAtAsync(GitRepository.Stage(stage), path, ct) : []);
            var local = await StageFileAsync(2, "Local");
            var remote = await StageFileAsync(3, "Remote");
            var @base = await StageFileAsync(1, "Base");
            return new Dictionary<string, string> { ["LOCAL"] = local, ["REMOTE"] = remote, ["BASE"] = @base, ["MERGED"] = merged };
        });

    static async Task RunAsync(RepoTabViewModel tab, ExternalTool tool, string jobName, string what,
        Func<string, string, Task>? showError, Func<string, CancellationToken, Task<Dictionary<string, string>>> prepare)
    {
        showError ??= tab.ShowError;
        if (tool.Exe is not { } exe || !ProgramPath.IsExecutable(exe))
        {
            await showError(ErrorTitle, $"Cannot find {what} at '{tool.ShownPath}'");
            return;
        }
        var job = new Job(jobName);
        tab.Jobs.Start(job);
        string? title = null, error = null;
        try
        {
            var values = await prepare(NewRunFolder(tab.Path), job.Token);
            if (!job.CancelRequested) // cancelled while the files were written: the tool does not start
            {
                var args = BuildArgs(tool.Args, values);
                job.AppendOutput($"Running '{string.Join(' ', args.Prepend(exe))}'\n");
                if (OpenIn.Start(exe, args, tab.Path) is { } reason) (title, error) = (ErrorTitle, $"Cannot run '{exe}'.\n{reason}");
            }
        }
        catch (Exception) when (job.CancelRequested) { } // a cancelled read is no error
        catch (GitException e) { (title, error) = ("Git Error", e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { (title, error) = (ErrorTitle, e.Message); }
        catch (Exception e)
        {
            Trace.WriteLine(e);
            (title, error) = (ErrorTitle, "[Internal]: " + e.Message); // as RunJobAsync
        }
        finally
        {
            if (error is null) job.Succeed(); // a requested cancel makes it Canceled
            else job.Fail(error);
            tab.Jobs.End(job);
        }
        if (error is not null) await showError(title!, error);
    }

    // ------------------------------------------------------------------ menus

    /// <summary>Menu items: one tool = `Diff in &lt;name&gt;` / `Merge in &lt;name&gt;` (Ctrl+D); several = the
    /// submenu `External Diff` / `External Merge` with one item per tool, the primary one showing Ctrl+D; none = nothing.</summary>
    public static List<MenuEntry> Entries(ToolKind kind, Func<ExternalTool, Task> run, bool enabled = true, bool gestures = true)
    {
        var tools = Visible(kind);
        var primary = Primary(tools);
        string? Gesture(ExternalTool t) => gestures && t == primary ? "Ctrl+D" : null;
        return tools switch
        {
            [] => [],
            [var t] => [Menus.Item($"{(kind == ToolKind.Diff ? "Diff" : "Merge")} in {t.Name}", () => run(t), Gesture(t), enabled)],
            _ => [Menus.Sub(kind == ToolKind.Diff ? "External Diff" : "External Merge",
                tools.Select(t => Menus.Item(t.Name, () => run(t), Gesture(t))).ToList(), enabled)],
        };
    }

    /// <summary>Ctrl+D: the primary visible tool, nothing when there is none.</summary>
    public static Task RunPrimaryAsync(ToolKind kind, Func<ExternalTool, Task> run) =>
        Primary(Visible(kind)) is { } t ? run(t) : Task.CompletedTask;

    /// <summary>Single commit (From null: the commit's first parent) or range: the file before and after.</summary>
    public static (DiffSide Left, DiffSide Right) CommitSides(string? from, string to, string path, string? oldPath, ChangeKind kind) =>
        (new(from ?? to + "~", oldPath ?? path, kind == ChangeKind.Added), new(to, path, kind == ChangeKind.Deleted));

    /// <summary>Local Changes: Unstaged = index → working tree; Staged = HEAD (HEAD's parent with Amend; empty
    /// when HEAD is a root commit, <paramref name="headHasParent"/> false) → index.</summary>
    public static (DiffSide Left, DiffSide Right) WorkingSides(StatusEntry e, bool amend, bool headHasParent = true) => e.Staged
        ? (new(amend ? "HEAD~" : "HEAD", e.OldPath ?? e.Path, e.Kind == ChangeKind.Added || (amend && !headHasParent)), new(GitRepository.IndexRev, e.Path, e.Kind == ChangeKind.Deleted))
        : (new(GitRepository.IndexRev, e.OldPath ?? e.Path, e.Kind is ChangeKind.Untracked or ChangeKind.Ignored), new(null, e.Path, e.Kind == ChangeKind.Deleted));

    /// <summary>The rows of a Local Changes list selection in list order.</summary>
    static List<FileRow> Rows(LocalChangesViewModel vm, bool staged)
    {
        var (sel, rows) = staged ? (vm.StagedSelection, vm.StagedRows) : (vm.UnstagedSelection, vm.UnstagedRows);
        return sel.SelectedIndexes.Where(i => i >= 0 && i < rows.Count).Order().Select(i => rows[i]).ToList();
    }

    static bool IsPlainFile(FileRow r) => r.Entry is { IsSubmodule: false } && !r.IsNestedRepository;

    /// <summary>Context-menu items for the unstaged or staged selection of one list.</summary>
    public static List<MenuEntry> LocalChangesEntries(LocalChangesViewModel vm, bool staged)
    {
        var rows = Rows(vm, staged);
        if (rows.Count == 0) return [];
        var first = rows[0];
        var tab = vm.Owner;
        if (staged)
            return first.Entry is { IsSubmodule: true } && rows.Count == 1 ? []
                : Entries(ToolKind.Diff, t => LocalDiffAsync(vm, first, t), enabled: rows.Count == 1 && IsPlainFile(first));
        if (!IsPlainFile(first)) return [];
        if (first.Kind == ChangeKind.Conflicted)
            return MergeAllowed(vm, first) ? Entries(ToolKind.Merge, t => MergeAsync(tab, t, first.Path)) : [];
        return Entries(ToolKind.Diff, t => LocalDiffAsync(vm, first, t), enabled: rows.Count == 1);
    }

    /// <summary>An unmerged row that is not modify/delete, as the conflict card's <see cref="UnmergedFile.MergeAllowed"/>;
    /// a row missing from the repository state counts as not allowed.</summary>
    static bool MergeAllowed(LocalChangesViewModel vm, FileRow row) =>
        vm.Owner.State.Unmerged.FirstOrDefault(f => f.Path == row.Path) is { MergeAllowed: true };

    static async Task LocalDiffAsync(LocalChangesViewModel vm, FileRow row, ExternalTool tool)
    {
        var e = row.Entry!;
        bool amend = vm.Amend && e.Staged;
        var (left, right) = WorkingSides(e, amend, !amend || await vm.Owner.Repo.HeadHasParentAsync());
        await DiffAsync(vm.Owner, tool, left, right);
    }

    /// <summary>Ctrl+D: the first selected file of the list; the merge tool for an unmerged one, else the diff
    /// tool. Nothing for a folder or when no tool is visible.</summary>
    public static Task LocalChangesShortcutAsync(LocalChangesViewModel vm, bool staged)
    {
        if (Rows(vm, staged) is not [var first, ..] || !IsPlainFile(first)) return Task.CompletedTask;
        if (!staged && first.Kind == ChangeKind.Conflicted)
            return MergeAllowed(vm, first) ? RunPrimaryAsync(ToolKind.Merge, t => MergeAsync(vm.Owner, t, first.Path)) : Task.CompletedTask;
        return RunPrimaryAsync(ToolKind.Diff, t => LocalDiffAsync(vm, first, t));
    }
}
