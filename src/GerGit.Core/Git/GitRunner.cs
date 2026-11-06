using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace GerGit.Core.Git;

/// <summary>Starts git processes.</summary>
public static partial class GitRunner
{
    // Invalid UTF-8 becomes U+FFFD, no BOM on stdin.
    static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false);

    /// <summary>Resolved git executable, absolute (<see cref="ProgramPath"/>): $GERGIT_GIT, else `git` on PATH. When it
    /// cannot be resolved it stays the name: nothing is started then and every call fails with "Cannot find git
    /// instance at: '&lt;name&gt;'".</summary>
    public static string GitPath { get; } =
        Environment.GetEnvironmentVariable("GERGIT_GIT") is { Length: > 0 } env ? ProgramPath.Of(env) ?? env : ProgramPath.Of("git") ?? "git";

    public static readonly Version MinimumVersion = new(2, 38);

    /// <summary>At least one hosting account exists (set by the app when the account list changes): every git process
    /// then gets the credential-helper override.</summary>
    public static bool CredentialHelperEnabled { get; set; }

    /// <summary>Absolute path of util-linux setsid, null when it is not on PATH. Every git process starts through it;
    /// the pseudo-terminal start needs it with -c (--ctty), and external programs (terminal, editors) start through it
    /// in their own session. Settable for a test of the pseudo-terminal fallback.</summary>
    public static string? SetsidPath { get; internal set; } = ProgramPath.Of("setsid");

    public static bool HasSetsid => SetsidPath is not null;

    /// <summary>Absolute path of coreutils env (resets the signal dispositions before git, see Start).</summary>
    static readonly string? EnvPath = ProgramPath.Of("env");

    // `manager` joins the chain only when Git Credential Manager is installed.
    static readonly bool HasCredentialManager = ProgramPath.Of("git-credential-manager") is not null;

    /// <summary>Run git, collect stdout/stderr as UTF-8. Never throws for a non-zero exit; cancellation yields Cancelled = true.</summary>
    public static Task<GitResult> RunAsync(string? workDir, IReadOnlyList<string> args, string? stdin = null, CancellationToken ct = default) =>
        RunProgramAsync(GitPath, workDir, args, stdin, silent: false, job: null, ct);

    /// <summary>
    /// Run git as a step of <paramref name="job"/>: writes the command header, streams stdout and
    /// non-progress stderr lines into the job output and progress lines into <see cref="Job.Report"/>. Cancel comes from
    /// <see cref="Job.Token"/> unless <paramref name="ignoreCancel"/> (steps that must run even after a cancel, e.g. the
    /// stash re-apply of a pull). <paramref name="silent"/> = no prompts (GERGIT_NO_PROMPT=1).
    /// <paramref name="header"/> false = only git's output (Git commands: fetch and push write no header).
    /// <paramref name="editor"/> = the command picks its editor with `-c core.editor=` / `-c sequence.editor=` (interactive
    /// rebase, rebase continue): GIT_EDITOR and GIT_SEQUENCE_EDITOR would override those, so they are left out.
    /// <paramref name="env"/> = extra environment variables for this command.
    /// <paramref name="tty"/> = git gets a pseudo-terminal as its controlling terminal (and stdin); what is written to it
    /// (/dev/tty of git and its hooks) goes to <see cref="Job.TtyText"/>. stdout and stderr stay pipes. Without a pty
    /// (allocation failed, or <see cref="PtyDisabled"/>) the command runs as without <paramref name="tty"/>.
    /// <paramref name="hooksWait"/> = the command runs hooks that clean up on SIGINT (pre-commit restores its stash of
    /// unstaged changes): a cancel interrupts the hooks but not git and waits this long for them (see
    /// <see cref="HooksWait"/>, CancelHooksAsync).
    /// The returned Stderr holds the non-progress lines only. The job is not finished here.
    /// </summary>
    public static Task<GitResult> RunJobAsync(Job job, string? workDir, IReadOnlyList<string> args, string? stdin = null,
        bool silent = false, bool ignoreCancel = false, bool header = true, bool editor = false,
        IReadOnlyDictionary<string, string>? env = null, bool tty = false, TimeSpan? hooksWait = null) =>
        RunProgramAsync(GitPath, workDir, args, stdin, silent, job, ignoreCancel ? CancellationToken.None : job.Token, header, editor, env, tty,
            hooksWait);

    /// <summary>The SIGINT wait of commands whose hooks the user watches (the dialog merge, the commit).</summary>
    public static readonly TimeSpan HooksWait = TimeSpan.FromSeconds(10);

    /// <summary>Tests: no pseudo-terminal, as when none can be allocated.</summary>
    internal static bool PtyDisabled { get; set; }

    /// <summary>Runs <paramref name="executable"/> (git, or a fake in tests) with the git environment.</summary>
    internal static async Task<GitResult> RunProgramAsync(string executable, string? workDir, IReadOnlyList<string> args,
        string? stdin, bool silent, Job? job, CancellationToken ct, bool header = true, bool editor = false,
        IReadOnlyDictionary<string, string>? env = null, bool tty = false, TimeSpan? hooksWait = null)
    {
        if (ct.IsCancellationRequested) return new GitResult(-1, "", "", Cancelled: true);
        if (job is not null && header) AppendHeader(job, executable, args);
        if (tty && !PtyDisabled && !HasSetsid) Trace.WriteLine("No setsid on PATH: the command runs without a pseudo-terminal");
        using var pty = tty && !PtyDisabled && HasSetsid ? Pty.Open() : null; // closed on every path
        using var process = Start(executable, workDir, args, stdin is not null, silent, out var startError, editor, env, pty?.SlavePath);
        if (process is null) return new GitResult(-1, "", startError!);
        using var stopTty = new CancellationTokenSource();
        var ttyRead = pty?.ReadAsync(text => job?.AppendTty(text), stopTty.Token) ?? Task.CompletedTask;

        int pid = process.Id;
        var exited = process.WaitForExitAsync();
        Task<string> stdout, stderr;
        Task? ladder = null;
        try
        {
            // Disposing the registration waits for a running callback, so `ladder` is set once the block is left.
            using (ct.Register(() => ladder = CancelAsync(pid, exited, hooksWait)))
            {
                if (job is null)
                {
                    stdout = process.StandardOutput.ReadToEndAsync();
                    stderr = process.StandardError.ReadToEndAsync();
                }
                else
                {
                    stdout = PumpAsync(process.StandardOutput, line => { job.AppendStdoutLine(line); return true; },
                        pending => job.PendingOutput = pending);
                    stderr = PumpAsync(process.StandardError, line =>
                    {
                        if (ParseProgress(line) is { } p) { job.Report(p.Percent, p.Message); return false; }
                        job.AppendStderrLine(line);
                        return true;
                    }, pending => job.PendingLine = pending);
                }
                await WriteStdinAsync(process, stdin);
                await exited;
                await Task.WhenAll(stdout, stderr);
            }
            if (ladder is not null) await ladder;
        }
        finally
        {
            if (pty is not null)
            {
                // git is gone (after a cancel its whole group): the master ends with EIO once no one holds the slave; a
                // descendant that left the group could hold it, so stop after a short drain. Awaited here, before the
                // descriptors and the token source are disposed.
                pty.CloseSlave();
                stopTty.CancelAfter(TimeSpan.FromMilliseconds(300));
                await ttyRead;
            }
        }

        var err = await stderr;
        // The pty start (sh → setsid) failed to find a program: once more without the pty, as without tty.
        if (pty is not null && process.ExitCode == 127 && !ct.IsCancellationRequested &&
            err.StartsWith("sh: ", StringComparison.Ordinal) && err.Contains("not found", StringComparison.Ordinal))
        {
            Trace.WriteLine($"The pseudo-terminal start failed ({err.Trim()}): the command runs without one");
            return await RunProgramAsync(executable, workDir, args, stdin, silent, job, ct, header: false, editor, env, tty: false, hooksWait);
        }
        // env (or setsid) could not exec the program (126/127): the git executable is missing.
        if (process.ExitCode is 126 or 127 &&
            (err.StartsWith("env: ", StringComparison.Ordinal) || err.StartsWith("setsid: ", StringComparison.Ordinal)))
            return new GitResult(-1, "", $"Cannot find git instance at: '{executable}'");
        return new GitResult(process.ExitCode, await stdout, err, ct.IsCancellationRequested);
    }

    /// <summary>Reads lines (split on \n, \r and \r\n); returns the kept ones joined with "\n".
    /// A line ends at its \r at once (ReadLineAsync would wait for the next char), so each progress update shows on time.
    /// <paramref name="pending"/> gets the text after the last line break after each read ("" at the end).</summary>
    static async Task<string> PumpAsync(StreamReader reader, Func<string, bool> keep, Action<string>? pending = null)
    {
        var text = new StringBuilder();
        var line = new StringBuilder();
        var buffer = new char[4096];
        bool afterCr = false;
        int n;
        while ((n = await reader.ReadAsync(buffer)) > 0)
        {
            for (int i = 0; i < n; i++)
            {
                char c = buffer[i];
                if (c == '\n' && afterCr) { afterCr = false; continue; } // the \n of a \r\n
                afterCr = c == '\r';
                if (c is '\r' or '\n') Emit();
                else line.Append(c);
            }
            pending?.Invoke(line.ToString());
        }
        if (line.Length > 0) Emit();
        pending?.Invoke("");
        return text.ToString();

        void Emit()
        {
            var l = line.ToString();
            line.Clear();
            if (keep(l)) text.Append(l).Append('\n');
        }
    }

    /// <summary>first match of `([A-z ]+):\s+(\d+)%`; the phase loses a leading "[K" and surrounding spaces.</summary>
    internal static (int Percent, string Message)? ParseProgress(string line)
    {
        var m = ProgressRegex().Match(line);
        if (!m.Success || !int.TryParse(m.Groups[2].Value, out var percent)) return null;
        var phase = m.Groups[1].Value.Trim();
        if (phase.StartsWith("[K", StringComparison.Ordinal)) phase = phase[2..].Trim();
        return (percent, $"{phase}: {percent}%");
    }

    [GeneratedRegex(@"([A-z ]+):\s+(\d+)%")]
    private static partial Regex ProgressRegex();

    /// <summary>The secret in a URL's user-info: `scheme://user:password@host` → `scheme://user:***@host`. For http(s) a
    /// user-info without a password is masked whole (`https://***@host`): tokens are often passed as the user name
    /// (`https://ghp_...@github.com`), and a plain user name cannot be told apart from one. Other schemes keep a plain user
    /// (`ssh://git@host`). Anything else is unchanged.</summary>
    internal static string MaskPassword(string arg) => UrlUserInfoRegex().Replace(arg, m =>
    {
        var (scheme, info) = (m.Groups[1].Value, m.Groups[2].Value);
        int colon = info.IndexOf(':');
        if (colon >= 0) return $"{scheme}{info[..colon]}:***@";
        return scheme.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? scheme + "***@" : m.Value;
    });

    /// <summary>Scheme with "://", then the user-info up to the last "@" before the path.</summary>
    [GeneratedRegex(@"([A-Za-z][A-Za-z0-9+.-]*://)([^/]*)@")]
    private static partial Regex UrlUserInfoRegex();

    /// <summary>blank line in front unless the output is empty or ends in two line breaks, then `$ git ...` and a blank line.
    /// Secrets in URLs are shown as `***` (<see cref="MaskPassword"/>).</summary>
    static void AppendHeader(Job job, string executable, IReadOnlyList<string> args)
    {
        var shown = string.Join(' ', args.Select(MaskPassword).Select(a => a.Length == 0 || a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a))
            .Replace("-c push.default=upstream ", "").Replace(" --progress", "");
        // The credential override goes through the environment (Start), so there is nothing to strip here.
        var output = job.Output;
        var gap = output.Length == 0 || output.EndsWith("\n\n", StringComparison.Ordinal) ? ""
            : output.EndsWith('\n') ? "\n" : "\n\n";
        job.AppendOutput($"{gap}$ {(executable == GitPath ? "git" : executable)} {shown}\n\n");
    }

    /// <summary>Stream stdout line by line (for long outputs such as the revision walk). Throws GitException on a non-zero exit.</summary>
    public static async IAsyncEnumerable<string> ReadLinesAsync(string? workDir, IReadOnlyList<string> args,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var process = Start(GitPath, workDir, args, hasStdin: false, silent: false, out var startError);
        if (process is null) throw new GitException(startError!);
        int pid = process.Id;
        // A read-only walk stopped by its consumer: no SIGINT ladder, kill the whole group at once (also unblocks a pending pipe read).
        using var reg = ct.Register(() => Signal(pid, SIGKILL));
        try
        {
            var stderr = process.StandardError.ReadToEndAsync(); // drained concurrently, else git blocks on a full pipe
            // ReadLineAsync splits on \n, \r and \r\n.
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
                yield return line;
            ct.ThrowIfCancellationRequested();
            await process.WaitForExitAsync(ct);
            var result = new GitResult(process.ExitCode, "", await stderr);
            if (!result.Success) throw new GitException(result.ReadableOutput);
        }
        finally
        {
            // Consumer stopped early or cancelled: do not leave git running.
            if (!process.HasExited) Signal(pid, SIGKILL);
        }
    }

    /// <summary>binary reads (blob content, images) get the raw bytes of stdout. Throws GitException on a
    /// non-zero exit.</summary>
    public static async Task<byte[]> ReadBytesAsync(string? workDir, IReadOnlyList<string> args, CancellationToken ct = default)
    {
        using var process = Start(GitPath, workDir, args, hasStdin: false, silent: false, out var startError);
        if (process is null) throw new GitException(startError!);
        int pid = process.Id;
        using var reg = ct.Register(() => Signal(pid, SIGKILL)); // read-only: no SIGINT ladder, like ReadLinesAsync
        var stderr = process.StandardError.ReadToEndAsync();
        using var bytes = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(bytes, CancellationToken.None); // ends when the kill closes the pipe
        await process.WaitForExitAsync(CancellationToken.None);
        ct.ThrowIfCancellationRequested();
        var result = new GitResult(process.ExitCode, "", await stderr);
        if (!result.Success) throw new GitException(result.ReadableOutput);
        return bytes.ToArray();
    }

    /// <summary>`git version` parsed; null when git cannot be run.</summary>
    public static async Task<Version?> GetVersionAsync() => (await ProbeVersionAsync()).Version;

    /// <summary><see cref="GetVersionAsync"/> plus, when the version is null, the failure text (git's stderr, or the
    /// start failure such as "Cannot find 'setsid' on PATH").</summary>
    public static async Task<(Version? Version, string Error)> ProbeVersionAsync()
    {
        var result = await RunAsync(null, ["version"]);
        var version = result.Success ? ParseVersion(result.Stdout) : null;
        return (version, version is null ? result.Stderr.Trim() : "");
    }

    /// <summary>text after "version"; vendor suffixes (".windows.1", " (Apple Git-…)") are ignored.</summary>
    internal static Version? ParseVersion(string output)
    {
        var parts = output.Trim().Split("version", 2);
        if (parts.Length != 2) return null;
        var m = VersionRegex().Match(parts[1].Trim());
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value),
            m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0) : null;
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionRegex();

    static Process? Start(string executable, string? workDir, IReadOnlyList<string> args, bool hasStdin, bool silent, out string? error,
        bool editor = false, IReadOnlyDictionary<string, string>? env = null, string? ttyPath = null)
    {
        // Own session and process group, no controlling terminal. We are not a group leader, so
        // setsid execs in place: the started pid is git's pid and also its process group id (checked by a test).
        // env (coreutils >= 8.31) then execs git with SIGINT/SIGTERM at their defaults: an app started with them ignored
        // (`&` in a script, nohup, some launchers) would pass SIG_IGN on and the cancel ladder's SIGINT would do nothing.
        // With a pty (ttyPath): sh opens the slave as stdin and execs `setsid --ctty`, which makes it the controlling
        // terminal of the new session. Every exec is in place, so the started pid is still git's pid and process group.
        // Absolute paths only (ProgramPath): a bare name would be looked up in the current directory first.
        if (!Path.IsPathRooted(executable) || SetsidPath is null || EnvPath is null)
        {
            error = !Path.IsPathRooted(executable) ? $"Cannot find git instance at: '{executable}'"
                : $"Cannot find '{(SetsidPath is null ? "setsid" : "env")}' on PATH";
            return null;
        }
        var psi = new ProcessStartInfo(ttyPath is null ? SetsidPath : "/bin/sh")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true, // closed right away when there is no payload = empty stdin
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = Utf8,
            StandardOutputEncoding = Utf8,
            StandardErrorEncoding = Utf8,
        };
        if (workDir is not null) psi.WorkingDirectory = workDir;
        if (ttyPath is not null)
            foreach (var a in (string[])["-c", "p=$1; s=$2; shift 2; exec \"$s\" -c \"$@\" <\"$p\"", "sh", ttyPath, SetsidPath])
                psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(EnvPath);
        psi.ArgumentList.Add("--default-signal=INT,TERM");
        // With a controlling terminal git is a session leader: when it exits the kernel sends SIGHUP to what is left of
        // its group (e.g. a background job of a post-merge hook), unlike the CLI where the shell leads the session. `env --ignore-signal=HUP` would not
        // keep it: git installs its own SIGHUP handler (tempfile cleanup), so its hooks start with the default again.
        psi.ArgumentList.Add(executable);
        foreach (var a in args) psi.ArgumentList.Add(a);

        // Run git with a neutral locale (message texts are matched in English).
        psi.Environment.Remove("LC_ALL");
        psi.Environment.Remove("LANGUAGE");
        psi.Environment["LC_MESSAGES"] = "C";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        if (editor)
        {
            psi.Environment.Remove("GIT_EDITOR");
            psi.Environment.Remove("GIT_SEQUENCE_EDITOR");
        }
        else psi.Environment["GIT_EDITOR"] = "true";
        // Askpass. GIT_ASKPASS stays unset: git falls back to SSH_ASKPASS (a user's own core.askPass wins).
        psi.Environment["GERGIT_PID"] = Environment.ProcessId.ToString();
        psi.Environment["GERGIT_NO_PROMPT"] = silent ? "1" : "0";
        // Silent (background fetch): Git Credential Manager, chained after our helper, must not open its own window either.
        if (silent) psi.Environment["GCM_INTERACTIVE"] = "never";
        if (workDir is not null) psi.Environment["GERGIT_REPO_PATH"] = workDir;
        else psi.Environment.Remove("GERGIT_REPO_PATH");
        if (Askpass.HelperPath is { } helper)
        {
            psi.Environment["SSH_ASKPASS"] = helper;
            psi.Environment["SSH_ASKPASS_REQUIRE"] = "force";
            // , also for other programs named git.
            if (CredentialHelperEnabled && (executable == GitPath || Path.GetFileName(executable) == "git"))
                AddCredentialOverride(psi.Environment, helper, HasCredentialManager);
        }
        if (env is not null)
            foreach (var (k, v) in env) psi.Environment[k] = v;
        // Not built yet: GIT_SSH_COMMAND with configured SSH keys and the GIT_TRACE* verbose output.

        try
        {
            var p = Process.Start(psi)!;
            if (!hasStdin) p.StandardInput.Close();
            error = null;
            return p;
        }
        catch (Win32Exception)
        {
            // A missing working directory also fails the start (ENOENT): a deleted or moved repo, not a missing git.
            error = workDir is not null && !Directory.Exists(workDir)
                ? $"It looks like '{workDir}' was deleted or moved."
                : $"Cannot find git instance at: '{executable}'";
            return null;
        }
    }

    /// <summary>
    /// `credential.helper=` (clears the configured helpers), the askpass script as helper, then `manager`
    /// when <paramref name="manager"/>, as GIT_CONFIG_COUNT/KEY_n/VALUE_n entries (same scope as `-c`, so command lines
    /// and job headers stay as they are). Entries already in <paramref name="env"/> are kept; ours follow them.
    /// The `!` form runs the quoted path through the shell: a bare value with spaces would not be one path.
    /// </summary>
    internal static void AddCredentialOverride(IDictionary<string, string?> env, string helperPath, bool manager)
    {
        int n = env.TryGetValue("GIT_CONFIG_COUNT", out var count) && int.TryParse(count, out var c) && c > 0 ? c : 0;
        string[] helpers = manager ? ["", "!" + Askpass.ShQuote(helperPath), "manager"] : ["", "!" + Askpass.ShQuote(helperPath)];
        foreach (var h in helpers)
        {
            env[$"GIT_CONFIG_KEY_{n}"] = "credential.helper";
            env[$"GIT_CONFIG_VALUE_{n}"] = h;
            n++;
        }
        env["GIT_CONFIG_COUNT"] = n.ToString();
    }

    static async Task WriteStdinAsync(Process process, string? stdin)
    {
        if (stdin is null) return;
        try
        {
            await process.StandardInput.WriteAsync(stdin);
            process.StandardInput.Close();
        }
        catch (IOException) { } // git exited without reading all input; its exit code tells the story
    }

    const int SIGINT = 2, SIGKILL = 9, SIGTERM = 15;

    /// <summary>SIGINT to the group, 2 s, SIGTERM, 2 s, SIGKILL. Once git has exited, whatever is left of
    /// its group (children that ignore the signal) is killed so the run's pipes close. With <paramref name="hooksWait"/>:
    /// <see cref="CancelHooksAsync"/>.</summary>
    static async Task CancelAsync(int pgid, Task exited, TimeSpan? hooksWait)
    {
        if (hooksWait is { } wait)
        {
            await CancelHooksAsync(pgid, exited, wait);
            return;
        }
        foreach (var sig in (int[])[SIGINT, SIGTERM])
        {
            Signal(pgid, sig);
            if (await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2))) == exited)
            {
                Signal(pgid, SIGKILL);
                return;
            }
        }
        Signal(pgid, SIGKILL);
    }

    /// <summary>
    /// The ladder for commands whose hooks clean up on SIGINT (pre-commit restores its stash of unstaged changes). git
    /// (the group leader, with a pty also the session leader) is left out of the SIGINT: it would die at once, and its
    /// end as session leader sends SIGHUP to the group, which kills pre-commit inside its restore. So: SIGINT to every
    /// other member, up to <paramref name="wait"/> for them to end; git then ends by itself (its hook failed). Only if
    /// git is still there: SIGINT to git, 2 s, SIGTERM to the group, 2 s; at the end SIGKILL to what is left.
    /// </summary>
    static async Task CancelHooksAsync(int pgid, Task exited, TimeSpan wait)
    {
        bool signalled = false;
        foreach (var pid in GroupMembers(pgid).Where(p => p != pgid))
            if (GroupOf(pid) == pgid) signalled |= kill(pid, SIGINT) == 0; // still ours right before the signal (pid reuse)
        var sw = Stopwatch.StartNew();
        while (signalled && sw.Elapsed < wait && !exited.IsCompleted && GroupMembers(pgid).Any(p => p != pgid)) await Task.Delay(50);
        // The hooks are done: git finishes its failed run (writes its state, exits) without any signal. No hook ran:
        // straight on to git.
        if (signalled && !exited.IsCompleted) await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2)));
        if (!exited.IsCompleted)
        {
            kill(pgid, SIGINT);
            await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        if (!exited.IsCompleted)
        {
            Signal(pgid, SIGTERM);
            await Task.WhenAny(exited, Task.Delay(TimeSpan.FromSeconds(2)));
        }
        Signal(pgid, SIGKILL); // whatever is left of the group (children that ignored the signals)
    }

    /// <summary>Live (not zombie) processes of process group <paramref name="pgid"/>, from /proc. Processes that come
    /// or go during the scan are simply seen or not.</summary>
    static List<int> GroupMembers(int pgid)
    {
        var members = new List<int>();
        foreach (var dir in Directory.EnumerateDirectories("/proc"))
            if (int.TryParse(Path.GetFileName(dir), out var pid) && GroupOf(pid) == pgid) members.Add(pid);
        return members;
    }

    /// <summary>The process group of a live process; null when it is gone or a zombie.</summary>
    static int? GroupOf(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' '); // state ppid pgrp ...
            return fields[0] != "Z" && int.TryParse(fields[2], out var pgrp) ? pgrp : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    static void Signal(int pgid, int sig) => kill(-pgid, sig); // ESRCH (group already gone) is fine

    [DllImport("libc", SetLastError = true)]
    static extern int kill(int pid, int sig);
}
