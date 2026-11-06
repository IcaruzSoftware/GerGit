using System.Diagnostics;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.ViewModels.Accounts;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;
using GerGit.Views.Dialogs;

namespace GerGit.ViewModels;

/// <summary>
/// The app's hosting state: the ONE account list and secret store, REST clients for the accounts, and the seams
/// later steps use (clone list, host icons, pull requests). <see cref="Current"/> is the app-wide instance; tests
/// create their own with a fake secret store, a temp accounts file and local base URLs.
/// </summary>
public sealed class HostingService(AccountStore store, ISecretStore secrets)
{
    static HostingService? s_current;

    /// <summary>The app-wide instance (set at startup). Without one, the user's accounts.json and the real keyring
    /// (only touched when an account is used). Tests that reach code using it set their own and restore it.</summary>
    public static HostingService Current
    {
        get => s_current ??= new(new AccountStore(AccountStore.DefaultFilePath), new LibSecretStore());
        set => s_current = value;
    }

    public AccountStore Store { get; } = store;
    public ISecretStore Secrets { get; } = secrets;

    /// <summary>Snapshot in list order.</summary>
    public IReadOnlyList<Account> Accounts => Store.Accounts;

    /// <summary>GitHub REST API base for this service's clients (tests: a local fake).</summary>
    public Uri GitHubApiBase { get; init; } = GitHubClient.DefaultApiBase;

    /// <summary>GitHub web base of the OAuth device flow (tests: a local fake).</summary>
    public Uri GitHubWebBase { get; init; } = GitHubDeviceFlow.DefaultWebBase;

    /// <summary>OAuth client id of the device flow; empty = OAuth not configured.</summary>
    public string GitHubClientId { get; init; } = GitHubDeviceFlow.ClientId;

    /// <summary>Opens a dialog modally; true = closed by submit. Tests drive the view model instead.</summary>
    public Func<DialogViewModel, Task<bool>> ShowDialog { get; set; } = vm => DialogWindow.ShowAsync(vm);

    /// <summary>Opens a web page in the default browser. Tests record the URL. Only absolute http(s) URLs without user
    /// info are passed on, in their checked form (<see cref="IssueTracker.WebUrl"/>), also to an action set by a test:
    /// links come from repository content (issue rules, commit messages, git output), and a `file:`, `javascript:` or
    /// other URL must never reach xdg-open.</summary>
    public Action<string> OpenBrowser
    {
        get;
        set => field = WebOnly(value);
    } = WebOnly(Launch);

    /// <summary>Opens a local folder, or a file with the desktop's default application. Paths may name files that come
    /// from a repository (working-tree files, files extracted from a commit), and xdg-open can launch `.desktop` files
    /// or executables depending on the desktop; so every path goes through <see cref="SafeToOpen"/> first (off the UI
    /// thread: it runs xdg-mime), also with an action set by a test. When a file's folder is shown instead,
    /// <see cref="ShowNotice"/> says why. The task ends once the path was handed on. Tests record the path.</summary>
    public Func<string, Task> OpenPath
    {
        get => path => OpenCheckedAsync(path, field);
        set;
    } = path =>
    {
        Launch(path);
        return Task.CompletedTask;
    };

    /// <summary>Tells the user something that needs no answer (a message window over the main window). Tests record it.</summary>
    public Func<string, Task> ShowNotice { get; set; } = text => MessageWindow.ShowAsync("File Not Opened", text);

    /// <summary>Shown after a file's folder was opened instead of the file.</summary>
    public const string FolderInsteadNotice =
        "Opened the folder: GerGit opens only passive file types (text, source code, common images, PDF) directly.";

    async Task OpenCheckedAsync(string path, Func<string, Task> open)
    {
        var (target, folderInstead) = await Task.Run(() => (SafeToOpen(path, out var instead), instead));
        if (target is null) return;
        await open(target);
        if (folderInstead) await ShowNotice(FolderInsteadNotice);
    }

    static Action<string> WebOnly(Action<string> open) => url =>
    {
        if (IssueTracker.WebUrl(url) is { } checkedUrl) open(checkedUrl);
        else Trace.WriteLine($"Not opened in the browser (not an http(s) URL): {url}");
    };

    /// <summary>
    /// What <see cref="OpenPath"/> really opens, null for nothing (logged). It must be rooted. The path is resolved with
    /// realpath(3) first, the same way the kernel does when xdg-open opens it (links, then `..`), and every check runs on
    /// that real target, which is also what gets opened; a path that does not resolve (dangling link, link loop) is
    /// refused. A folder is opened as it is. A regular file is opened only when all of these hold:
    /// - both its name and its real target end in one of <see cref="PassiveExtensions"/> (any case), have no extension
    ///   (`LICENSE`) or are dotfiles (`.gitignore`); scripts, web pages, XML, Markdown and the like never open;
    /// - it has no execute bit; its first 8000 bytes hold no `Desktop Entry]` (also `[KDE Desktop Entry]`) or
    ///   `[Desktop Action` and it does not start with `# Config File` / `# KDE Config File` (the desktop-entry MIME type
    ///   matches those) or with `MZ` (a Windows program, which Wine runs by its content); a text file has no NUL byte;
    /// - the MIME type that `xdg-mime query filetype` reports (<see cref="MimeTypeOf"/>) is one of
    ///   <see cref="PassiveTypes"/>. xdg-open asks the same source (gio on GNOME and similar, KDE's own lookup, else
    ///   `mimetype` or `file`, which go by the content), so a passive name with content that this desktop would hand to
    ///   a browser, Wine or an installer is refused; when xdg-mime is missing, fails or takes longer than 2 s, too.
    /// Anything else (any other file, a FIFO, device or socket) shows its (real) folder instead, with
    /// <paramref name="folderInstead"/> set.
    /// Limitation: checked before xdg-open runs, so a file swapped in between is opened unchecked.
    /// </summary>
    internal static string? SafeToOpen(string path, out bool folderInstead)
    {
        folderInstead = false;
        if (!Path.IsPathRooted(path))
        {
            Trace.WriteLine($"Not opened (not an absolute path): {path}");
            return null;
        }
        var target = RepositoryScan.RealPath(path);
        if (target is not null && Directory.Exists(target)) return target;
        if ((target is null ? "the path does not resolve" : WhyNotOpened(path, target)) is not { } reason) return target;
        var folder = Path.GetDirectoryName(path) is { } parent ? RepositoryScan.RealPath(parent) : null;
        Trace.WriteLine($"Not opened with the default application ({reason}): {path}; showing its folder instead");
        folderInstead = folder is not null && Directory.Exists(folder);
        return folderInstead ? folder : null;
    }

    static readonly HashSet<string> PassiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".log", ".rst", ".json", ".yaml", ".yml", ".toml", ".diff", ".patch",
        ".c", ".h", ".cc", ".cpp", ".cxx", ".hh", ".hpp", ".cs", ".java", ".kt", ".go", ".rs", ".css", ".scss", ".sql",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf",
    };

    static readonly string[] BinaryExtensions = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".pdf"];

    /// <summary>MIME types opened with the default application (names from shared-mime-info and from `file`).</summary>
    static readonly HashSet<string> PassiveTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "text/plain", "text/x-c", "text/x-csrc", "text/x-chdr", "text/x-c++", "text/x-c++src", "text/x-c++hdr", "text/x-csharp",
        "text/x-java", "text/x-kotlin", "text/x-go", "text/rust", "text/css", "text/x-scss", "text/x-log", "text/x-rst",
        "text/x-diff", "text/x-patch",
        "application/sql", "application/json", "application/yaml", "application/x-yaml", "application/toml", "application/pdf",
        "image/png", "image/jpeg", "image/gif", "image/webp",
    };

    /// <summary>The MIME type of a file as `xdg-mime query filetype` reports it (up to a `;`), null when xdg-mime is
    /// missing, fails or takes longer than 2 s. Tests replace it.</summary>
    internal static Func<string, string?> MimeTypeOf { get; set; } = QueryMimeType;

    internal static string? QueryMimeType(string path)
    {
        // Absolute path (ProgramPath), no shell, the path as its own argument.
        if (ProgramPath.Of("xdg-mime") is not { } xdgMime) return null;
        try
        {
            using var process = Process.Start(new ProcessStartInfo(xdgMime, ["query", "filetype", path])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(2000) || !output.Wait(500))
            {
                process.Kill(entireProcessTree: true);
                Trace.WriteLine($"xdg-mime took too long for {path}");
                return null;
            }
            return process.ExitCode == 0 && output.Result.Split(';')[0].Trim() is { Length: > 0 } type ? type : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Trace.WriteLine($"xdg-mime failed for {path}: {e.Message}");
            return null;
        }
    }

    static string? WhyNotOpened(string path, string target)
    {
        if (!RepositoryScan.IsRegularFile(target)) return "not a regular file";
        string[] extensions = [Path.GetExtension(path), Path.GetExtension(target)];
        // No extension (LICENSE) and dotfiles (.gitignore) are left to the MIME type below.
        bool Passive(string name) => PassiveExtensions.Contains(Path.GetExtension(name)) || Path.GetExtension(name).Length == 0
            || Path.GetFileNameWithoutExtension(name).Length == 0;
        if (!Passive(path) || !Passive(target)) return "not a file type that is safe to open";
        try
        {
            if (!OperatingSystem.IsWindows()
                && (File.GetUnixFileMode(target) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return "executable";
            var buffer = new byte[8000];
            int n;
            using (var stream = File.OpenRead(target)) n = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            var head = buffer.AsSpan(0, n);
            if (head.StartsWith("MZ"u8)) return "a Windows program"; // Wine sniffs the content, not the name
            if (!extensions.Any(e => BinaryExtensions.Contains(e, StringComparer.OrdinalIgnoreCase)) && head.Contains((byte)0))
                return "binary content in a text file";
            var text = System.Text.Encoding.ASCII.GetString(head);
            // The desktop-entry MIME type also matches files that start with "# Config File" / "# KDE Config File".
            if (text.Contains("Desktop Entry]") || text.Contains("[Desktop Action")
                || text.StartsWith("# Config File", StringComparison.Ordinal) || text.StartsWith("# KDE Config File", StringComparison.Ordinal))
                return "a desktop entry";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return e.Message;
        }
        return MimeTypeOf(target) is { } type
            ? PassiveTypes.Contains(type) ? null : $"MIME type {type}"
            : "unknown MIME type";
    }

    /// <summary>The app-wide notification list and its bell; one per account list.</summary>
    public NotificationsViewModel Notifications => field ??= new(this);

    /// <summary>File > Accounts...; also the target of "Credential Manager".</summary>
    public Task OpenAccountsAsync() => ShowDialog(new AccountsViewModel(this));

    /// <summary>The REST client for a typed token (login windows, before the account exists).</summary>
    public HostingClient Client(HostType type, string serverUrl, string token, string? caFile = null) =>
        type == HostType.GitHub ? new GitHubClient(token) { ApiBase = GitHubApiBase } : HostingClient.For(type, serverUrl, token, caFile);

    /// <summary>The client of an account. Reads the keyring (blocking, off the UI thread);
    /// a missing token throws <see cref="SecretStoreException"/>.</summary>
    public Task<HostingClient> ClientAsync(Account account) =>
        Task.Run(() => Client(account.Type, account.ServerUrl, Secrets.TokenFor(account), account.CaFile));

    public GitHubDeviceFlow DeviceFlow() => new(GitHubClientId) { WebBase = GitHubWebBase };

    /// <summary>
    /// Points the process-wide git hooks at this service and keeps them in sync with the account list (credential helper and askpass):
    /// the credential-helper override while accounts exist, the credential `get` answer, and the GitHub hints of
    /// the git error texts. Tests that call it restore <see cref="GitRunner.CredentialHelperEnabled"/>,
    /// <see cref="Askpass.LookupCredential"/>, <see cref="GitErrors.GitHubAccountCount"/> and <see cref="GitErrors.GitHubAccessUrl"/>.
    /// </summary>
    public void Install()
    {
        Store.Changed += SyncGitRunner;
        SyncGitRunner();
        Askpass.LookupCredential = q => Task.Run(() => LookupCredential(q));
        GitErrors.GitHubAccountCount = () => Store.Accounts.Count(a => a.Type == HostType.GitHub);
        if (GitHubClientId.Length > 0)
            GitErrors.GitHubAccessUrl = "https://github.com/settings/connections/applications/" + GitHubClientId;
    }

    void SyncGitRunner() => GitRunner.CredentialHelperEnabled = Store.Accounts.Count > 0;

    CredentialAnswer? LookupCredential(CredentialQuery q)
    {
        try
        {
            return Credentials.Lookup(Store.Accounts, Secrets, q.Protocol, q.Host, q.UserName) is { } found
                ? new CredentialAnswer(found.Username, found.Token) : null;
        }
        catch (Exception e)
        {
            // Never with the token: only the request and the failure's type and message (keyring texts carry no secret).
            Trace.WriteLine($"Credential lookup for {q.Protocol}://{q.Host} failed: {e.GetType().Name}: {e.Message}");
            return null;
        }
    }

    static void Launch(string url)
    {
        // Absolute path (ProgramPath): a bare name would be looked up in the current directory first.
        if (ProgramPath.Of("xdg-open") is not { } xdgOpen)
        {
            Trace.WriteLine($"Cannot open {url}: xdg-open is not on PATH");
            return;
        }
        try { Process.Start(new ProcessStartInfo(xdgOpen, [url]) { UseShellExecute = false })?.Dispose(); }
        catch (Exception e) { Trace.WriteLine($"Cannot open {url}: {e.Message}"); }
    }
}
