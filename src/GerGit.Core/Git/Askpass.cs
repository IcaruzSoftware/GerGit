using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace GerGit.Core.Git;

/// <summary>
/// The askpass helper and its server, for credential and SSH key prompts. git/ssh run the
/// SSH_ASKPASS script, which starts GerGit in `--askpass` mode; that helper sends the prompt over a Unix socket named
/// after the app pid and prints the app's answer. One request and one answer per connection: the helper writes
/// `kind \0 silent \0 repo path \0 payload` (UTF-8) and half-closes, the app writes the answer and closes.
/// The same script is git's credential helper (`&lt;script&gt; get`): kind `credential`, payload = git's stdin.
/// </summary>
public static partial class Askpass
{
    const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>UI for credential prompts; answers the typed text, null/empty = cancelled. Called on a socket thread:
    /// the delegate marshals to the UI thread itself. Default: no UI, every prompt is refused.</summary>
    public static Func<CredentialPrompt, Task<string?>> PromptCredentials { get; set; } = _ => Task.FromResult<string?>(null);

    /// <summary>UI for an unknown SSH host key. Same threading rule. Default: Cancel.</summary>
    public static Func<HostKeyPrompt, Task<HostKeyAnswer>> PromptHostKey { get; set; } = _ => Task.FromResult(HostKeyAnswer.Cancel);

    /// <summary>Credential-helper `get` (How the helper talks to the app, HTTPS git operations):
    /// the account answer for git's request, null = none. Never a dialog, also for silent requests. Called on a socket
    /// thread. Default: no accounts.</summary>
    public static Func<CredentialQuery, Task<CredentialAnswer?>> LookupCredential { get; set; } = _ => Task.FromResult<CredentialAnswer?>(null);

    const string CredentialRequest = "credential";

    /// <summary>The SSH_ASKPASS script while the server runs, else null.</summary>
    public static string? HelperPath { get; private set; }

    /// <summary>The interactive-rebase editor script (`GerGit --rebase-editor`, Editor helper behaviour) while
    /// the server runs, else null. Its todo requests come over the same socket.</summary>
    public static string? EditorPath { get; private set; }

    /// <summary>Where ssh adds accepted host keys; tests point it at a temp folder.</summary>
    internal static string KnownHostsFile { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "known_hosts");

    static Socket? _listener;
    static string? _socketPath;

    /// <summary>Starts the server and writes the helper script. Failures are only logged: git then runs without askpass.</summary>
    public static void Start()
    {
        if (_listener is not null || OperatingSystem.IsWindows()) return;
        Socket? listener = null;
        string? socketPath = null;
        try
        {
            socketPath = SocketPath(Environment.ProcessId);
            File.Delete(socketPath); // left behind by a crashed process with the same pid
            listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(socketPath));
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen();

            // SSH_ASKPASS takes one executable path without arguments, hence a script. The GerGit apphost lives next to
            // the assemblies (also in the test output folder); a single-file build is that file itself. Without it the
            // running dotnet host by its absolute path (`dotnet GerGit.dll`): a bare name would be searched on PATH.
            var appHost = Path.Combine(AppContext.BaseDirectory, "GerGit");
            string[] command = File.Exists(appHost) ? [appHost]
                : [Environment.ProcessPath ?? "dotnet", Path.Combine(AppContext.BaseDirectory, "GerGit.dll")];
            var script = Path.ChangeExtension(socketPath, ".askpass");
            File.WriteAllText(script, $"#!/bin/sh\nexec {string.Join(' ', command.Select(ShQuote))} --askpass \"$@\"\n");
            File.SetUnixFileMode(script, Private);
            var editor = Path.ChangeExtension(socketPath, ".editor");
            File.WriteAllText(editor, $"#!/bin/sh\nexec {string.Join(' ', command.Select(ShQuote))} --rebase-editor \"$@\"\n");
            File.SetUnixFileMode(editor, Private);

            (_listener, _socketPath, HelperPath, EditorPath) = (listener, socketPath, script, editor);
            _ = AcceptLoopAsync(listener);
        }
        catch (Exception e) // e.g. a socket path over 107 bytes: the app must still start
        {
            Trace.WriteLine($"Askpass server not started: {e.Message}");
            listener?.Dispose();
            if (listener is not null && socketPath is not null) File.Delete(socketPath);
        }
    }

    public static void Stop()
    {
        if (_listener is not { } listener) return;
        listener.Dispose();
        File.Delete(_socketPath!);
        File.Delete(HelperPath!);
        File.Delete(EditorPath!);
        (_listener, _socketPath, HelperPath, EditorPath) = (null, null, null, null);
    }

    /// <summary>$XDG_RUNTIME_DIR/gergit-&lt;pid&gt;.sock when that folder is private, else
    /// &lt;config dir&gt;/run/gergit-&lt;host&gt;-&lt;pid&gt;.sock (see <see cref="RuntimeDir(out bool)"/>). Typed
    /// passwords go over the socket and the helper scripts sit next to it, so only a folder nobody else can enter will do
    /// (some containers point XDG_RUNTIME_DIR at /tmp).</summary>
    [UnsupportedOSPlatform("windows")]
    internal static string SocketPath(int pid)
    {
        var dir = RuntimeDir(out var inConfigDir);
        return inConfigDir ? HostSocketPath(dir, $"-{pid}") : Path.Combine(dir, $"gergit-{pid}.sock");
    }

    /// <summary>&lt;dir&gt;/gergit-&lt;host&gt;&lt;suffix&gt;.sock for a folder that may be shared between hosts
    /// (NFS home): another host's GerGit must not reach or remove it. The host name is cut so the path stays within
    /// the 107 bytes of a Unix socket address.</summary>
    internal static string HostSocketPath(string dir, string suffix)
    {
        var host = Environment.MachineName; // ASCII
        var path = Path.Combine(dir, $"gergit-{host}{suffix}.sock");
        int over = System.Text.Encoding.UTF8.GetByteCount(path) - 107;
        return over <= 0 ? path : Path.Combine(dir, $"gergit-{host[..Math.Max(0, host.Length - over)]}{suffix}.sock");
    }

    /// <summary>$XDG_RUNTIME_DIR when that folder is private, else the private folder &lt;config dir&gt;/run (also
    /// used by <see cref="SingleInstance"/>). Never a shared temp folder: another user could take that over.
    /// As root (sudo keeps the user's HOME and XDG variables) none of them count: /run/user/0, else
    /// /root/.config/gergit/run.</summary>
    /// <param name="inConfigDir">true for the &lt;config dir&gt;/run fallback, which may be shared between hosts (NFS home).</param>
    [UnsupportedOSPlatform("windows")]
    internal static string RuntimeDir(out bool inConfigDir)
    {
        inConfigDir = false;
        if (Environment.IsPrivilegedProcess)
        {
            if (Directory.Exists("/run/user/0") && IsPrivate("/run/user/0")) return "/run/user/0";
            inConfigDir = true;
            return RunDir(ConfigFolder.RootLocation);
        }
        var dir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (dir is { Length: > 0 } && IsPrivate(dir)) return dir;
        inConfigDir = true;
        return RunDir(ConfigFolder.Location);
    }

    /// <summary>&lt;config dir&gt;/run, private: the config folder is made private first (<see cref="ConfigFolder.Create"/>).</summary>
    [UnsupportedOSPlatform("windows")]
    internal static string RunDir(string configDir) => PrivateDir(Path.Combine(ConfigFolder.Create(configDir), "run"));

    /// <summary>Creates <paramref name="dir"/> with mode 0700 if missing; throws unless it is private to this user.</summary>
    [UnsupportedOSPlatform("windows")]
    internal static string PrivateDir(string dir)
    {
        Directory.CreateDirectory(dir, Private);
        if (!IsPrivate(dir)) throw new IOException($"'{dir}' is not a private (0700) folder of this user");
        return dir;
    }

    /// <summary>Not a symlink (lstat), mode 0700, in a folder only its owner may write to (else the folder could be
    /// renamed away and replaced), and ours: .NET has no owner API, but only the owner (or root) may chmod, so a chmod
    /// to the mode it already has proves it.</summary>
    [UnsupportedOSPlatform("windows")]
    static bool IsPrivate(string dir)
    {
        try
        {
            if (new DirectoryInfo(dir).LinkTarget is not null) return false;
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(dir)) is { } parent &&
                (File.GetUnixFileMode(parent) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0) return false;
            // setgid/setuid/sticky do not open the folder (a run folder may inherit setgid: 02700).
            var mode = File.GetUnixFileMode(dir);
            if ((mode & ~(UnixFileMode.SetGroup | UnixFileMode.SetUser | UnixFileMode.StickyBit)) != Private) return false;
            File.SetUnixFileMode(dir, mode);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static string ShQuote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    static async Task AcceptLoopAsync(Socket listener)
    {
        while (true)
        {
            Socket client;
            try { client = await listener.AcceptAsync(); }
            catch (Exception e) when (e is SocketException or ObjectDisposedException) { return; } // stopped
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    static async Task ServeAsync(Socket client)
    {
        Func<Task>? after = null;
        try
        {
            using (client)
            using (var stream = new NetworkStream(client))
            {
                var request = new MemoryStream();
                await stream.CopyToAsync(request); // ends with the helper's half-close
                var f = Encoding.UTF8.GetString(request.ToArray()).Split('\0', 4);
                var answer = "";
                if (f.Length == 4) (answer, after) = await AnswerAsync(f[0], f[1] == "1", f[2], f[3]);
                await stream.WriteAsync(Encoding.UTF8.GetBytes(answer));
            }
            // Runs after the answer went out: ssh only writes known_hosts once it has its "yes".
            if (after is not null) await after();
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Askpass request failed: {e}"); // the helper sees an empty answer and refuses
        }
    }

    /// <summary>The app-side handler. Returns the answer and an optional
    /// action to run once the answer is sent.</summary>
    internal static async Task<(string Answer, Func<Task>? After)> AnswerAsync(string kind, bool silent, string repoPath, string text)
    {
        if (kind == RebaseEditor.TodoRequest) return (await RebaseEditor.AnswerTodoAsync(repoPath, text), null);
        if (kind == CredentialRequest) return (await AnswerCredentialAsync(repoPath, text), null);
        if (kind != "askpass") return ("", null);

        // An unknown host key is never accepted silently; silent requests refuse it.
        if (IsHostKeyQuestion(text))
        {
            var choice = silent ? HostKeyAnswer.Cancel : await PromptHostKey(new HostKeyPrompt(repoPath, text));
            if (choice == HostKeyAnswer.Cancel) return ("no", null);
            if (choice == HostKeyAnswer.Always) return ("yes", null);
            // Once: ssh has no "connect without saving" answer, so undo what it appends to known_hosts.
            // Limitation: only the default ~/.ssh/known_hosts is watched; with a custom UserKnownHostsFile the key stays.
            var path = KnownHostsFile;
            var before = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
            return ("yes", () => RemoveAppendedAsync(path, before));
        }

        // Not built yet: keyring lookup for ssh passphrases/passwords (comes with the prompt's
        // "Remember" box): every prompt asks.
        if (silent) return ("", null);
        return (await PromptCredentials(ParsePrompt(repoPath, text)) ?? "", null);
    }

    /// <summary>Git's `protocol=` / `host=` / `username=` lines in, git's credential format out (empty = not found).
    /// The host is echoed as git sent it (`host:port` included); the user name is always the account's.</summary>
    static async Task<string> AnswerCredentialAsync(string repoPath, string text)
    {
        var fields = new Dictionary<string, string>();
        foreach (var line in text.Split('\n'))
            if (line.Split('=', 2) is [var key, var value]) fields.TryAdd(key, value);
        if (!fields.TryGetValue("protocol", out var protocol) || !fields.TryGetValue("host", out var host) ||
            protocol.Length == 0 || host.Length == 0) return "";
        var found = await LookupCredential(new(protocol, host, fields.GetValueOrDefault("username"), repoPath));
        // A line break in a value would let it inject protocol lines; git refuses such values too.
        if (found is null || (found.UserName + found.Password).IndexOfAny(['\n', '\0']) >= 0) return "";
        return $"protocol={protocol}\nhost={host}\nusername={found.UserName}\npassword={found.Password}\n\n";
    }

    internal static bool IsHostKeyQuestion(string text) =>
        (text.Contains("The authenticity of host", StringComparison.Ordinal) ||
         text.Contains("differs from the key for the IP address", StringComparison.Ordinal)) &&
        text.Contains("Are you sure you want to continue connecting", StringComparison.Ordinal);

    /// <summary>Waits (up to 10 s) until ssh has appended its line(s) to <paramref name="path"/>, then cuts the file back
    /// to <paramref name="before"/> byte for byte. A file rewritten in the meantime is left alone.
    /// Limitation: lines another process appends in the same moment are removed too.</summary>
    internal static async Task RemoveAppendedAsync(string path, byte[]? before)
    {
        int length = before?.Length ?? 0;
        for (int i = 0; i < 100; i++)
        {
            await Task.Delay(100);
            if (!File.Exists(path)) continue;
            var now = await File.ReadAllBytesAsync(path);
            if (now.Length <= length || now[^1] != '\n') continue; // nothing (complete) appended yet
            if (before is not null && !now.AsSpan(0, length).SequenceEqual(before)) return;
            using var file = new FileStream(path, FileMode.Open, FileAccess.Write);
            file.SetLength(length);
            return;
        }
    }

    /// <summary>Recognised prompts.</summary>
    internal static CredentialPrompt ParsePrompt(string repoPath, string text)
    {
        if (text.StartsWith("Username for", StringComparison.Ordinal))
            return new(repoPath, PromptKind.UserName, text, null);
        if (PassphraseRegex().Match(text) is { Success: true } key)
            return new(repoPath, PromptKind.SshPassphrase, text, key.Groups[1].Value);
        // user@host parsed as an ssh:// URL; without a user name the prompt counts as unparseable (generic password).
        if (SshPasswordRegex().Match(text) is { Success: true } pw &&
            Uri.TryCreate("ssh://" + pw.Groups[1].Value, UriKind.Absolute, out var uri) && uri.UserInfo.Length > 0)
            return new(repoPath, PromptKind.SshPassword, text, pw.Groups[1].Value);
        if (text.StartsWith("Enter passphrase", StringComparison.Ordinal))
            return new(repoPath, PromptKind.OtherPassphrase, text, null);
        return new(repoPath, PromptKind.Password, text, null);
    }

    [GeneratedRegex("""Enter\s+passphrase\s*for\s*key\s*['"]([^'"]+)['"]:\s*""", RegexOptions.IgnoreCase)]
    private static partial Regex PassphraseRegex();

    [GeneratedRegex(@"(\S+)'s\s+password:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex SshPasswordRegex();

    // ---- Helper side (a separate GerGit process started by git/ssh) ----

    /// <summary>`GerGit --askpass &lt;args&gt;`: answers on stdout without newline and exit 0, or exit non-zero.
    /// `get` / `store` / `erase` = credential-helper mode: `get` forwards git's stdin and prints the answer (empty = exit 1,
    /// git goes on with the next helper or the askpass prompt); store and erase do nothing.</summary>
    public static int RunHelper(string[] args)
    {
        if (args is ["store"] or ["erase"]) return 0;
        if (OperatingSystem.IsWindows()) return 1;
        var (kind, payload) = args is ["get"]
            ? (CredentialRequest, new StreamReader(Console.OpenStandardInput(), Encoding.UTF8).ReadToEnd())
            : ("askpass", string.Join(' ', args));

        var pidText = Environment.GetEnvironmentVariable("GERGIT_PID");
        using var socket = int.TryParse(pidText, out var pid) ? Connect(pid, TimeSpan.FromSeconds(100)) : null;
        if (socket is null)
        {
            Console.Error.WriteLine($"GerGit process not found (pid '{pidText}')");
            return 1;
        }
        var silent = Environment.GetEnvironmentVariable("GERGIT_NO_PROMPT") == "1";
        var repoPath = Environment.GetEnvironmentVariable("GERGIT_REPO_PATH") ?? "";
        using var stream = new NetworkStream(socket);
        stream.Write(Encoding.UTF8.GetBytes(string.Join('\0', kind, silent ? "1" : "0", repoPath, payload)));
        socket.Shutdown(SocketShutdown.Send);
        var answer = new MemoryStream();
        stream.CopyTo(answer);
        if (answer.Length == 0) return 1;
        // Raw UTF-8 bytes: Console.Out would follow the locale and break non-ASCII passwords.
        using var stdout = Console.OpenStandardOutput();
        answer.WriteTo(stdout);
        return 0;
    }

    /// <summary>Retries for up to <paramref name="timeout"/> (100 s) while the app process exists.</summary>
    [UnsupportedOSPlatform("windows")]
    internal static Socket? Connect(int pid, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                socket.Connect(new UnixDomainSocketEndPoint(SocketPath(pid)));
                return socket;
            }
            catch (Exception e) when (e is SocketException or IOException)
            {
                socket.Dispose();
            }
            if (sw.Elapsed >= timeout || !Directory.Exists($"/proc/{pid}")) return null;
            Thread.Sleep(200);
        }
    }
}

/// <summary>A credential-helper `get` from git. Host exactly as git sent it (`host:port` when the port is not the
/// scheme's default); UserName = git's `username=` line, if any; RepoPath may be empty.</summary>
public sealed record CredentialQuery(string Protocol, string Host, string? UserName, string RepoPath);

/// <summary>The account's user name and token for <see cref="Askpass.LookupCredential"/>.</summary>
public sealed record CredentialAnswer(string UserName, string Password)
{
    public override string ToString() => $"CredentialAnswer {{ UserName = {UserName} }}"; // the token never lands in a log
}
