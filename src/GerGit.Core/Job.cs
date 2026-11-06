namespace GerGit.Core;

// Shared contract (owned by the orchestrator): implementers may ADD members, but must not rename or remove existing ones.

/// <summary>Observable outcome of a job. Canceled = the cancelled work has ended.</summary>
public enum JobOutcome { Running, Succeeded, Failed, Canceled }

/// <summary>
/// One unit of background work with a name. Written from worker threads, read by the UI on a
/// 200 ms timer, so every member is thread-safe and there are no change events.
/// </summary>
public sealed class Job(string name, bool hidden = false)
{
    readonly object _lock = new();
    readonly CancellationTokenSource _cts = new();
    readonly System.Text.StringBuilder _output = new();
    readonly System.Text.StringBuilder _tty = new();
    int? _progress;
    string _message = "";
    string _pendingLine = "";
    string _pendingOut = "";
    JobOutcome _outcome = JobOutcome.Running;
    DateTime? _finished;

    public string Name { get; } = name;

    /// <summary>Hidden jobs (refresh, probes) are not kept in the history and never shown in the status box.</summary>
    public bool Hidden { get; } = hidden;

    /// <summary>A background job (the per-remote automatic fetch): listed under the Activity Manager's
    /// Background tab, shown in the status box only after 500 ms of running and not after it finished.</summary>
    public bool Background { get; init; }

    public DateTime Started { get; } = DateTime.UtcNow;
    public CancellationToken Token => _cts.Token;
    public bool CancelRequested => _cts.IsCancellationRequested;

    public int? Progress { get { lock (_lock) return _progress; } }
    public string Message { get { lock (_lock) return _message; } }
    public string Output { get { lock (_lock) return _output.ToString(); } }
    public JobOutcome Outcome { get { lock (_lock) return _outcome; } }
    public DateTime? Finished { get { lock (_lock) return _finished; } }
    public bool IsRunning => Outcome == JobOutcome.Running;

    /// <summary>stderr text of the running git step after its last line break (not yet in <see cref="Output"/>), e.g.
    /// pre-commit's "name......" before the hook's status; "" when there is none.</summary>
    public string PendingLine { get { lock (_lock) return _pendingLine; } internal set { lock (_lock) _pendingLine = value; } }

    /// <summary>The same for stdout: a pre-push hook writes to git's stdout (pre-commit's "name......").</summary>
    internal string PendingOutput { set { lock (_lock) _pendingOut = value; } }

    /// <summary><see cref="Output"/> and the pending line (stderr's, else stdout's) read together: a line moves from one
    /// to the other in one step (<see cref="AppendStderrLine"/>, <see cref="AppendStdoutLine"/>), so a reader never sees
    /// it twice or not at all.</summary>
    public (string Output, string PendingLine) Snapshot()
    {
        lock (_lock) return (_output.ToString(), _pendingLine.Length > 0 ? _pendingLine : _pendingOut);
    }

    /// <summary>What git and its hooks wrote to their terminal (a run with a pseudo-terminal, the Merge dialog's merge).
    /// Raw: ANSI codes and \r as written. Not part of <see cref="Output"/>.</summary>
    public string TtyText { get { lock (_lock) return _tty.ToString(); } }

    internal void AppendTty(string text)
    {
        lock (_lock) _tty.Append(text);
    }

    internal void AppendStderrLine(string line)
    {
        lock (_lock) { _output.Append(line).Append('\n'); _pendingLine = ""; }
    }

    internal void AppendStdoutLine(string line)
    {
        lock (_lock) { _output.Append(line).Append('\n'); _pendingOut = ""; }
    }

    /// <summary>Requests cancel once; the git runner reacts through <see cref="Token"/>.</summary>
    public void Cancel()
    {
        if (CancelRequested || !IsRunning) return;
        lock (_lock) _message = "canceled";
        _cts.Cancel();
    }

    /// <summary>Progress 0-100 or null (no bar), plus the one-line message.</summary>
    public void Report(int? progress, string message)
    {
        lock (_lock) { _progress = progress; _message = message; }
    }

    public void SetMessage(string message)
    {
        lock (_lock) _message = message;
    }

    public void AppendOutput(string text)
    {
        lock (_lock) _output.Append(text);
    }

    public void Succeed(string message = "") => Finish(JobOutcome.Succeeded, message);
    public void Fail(string message) => Finish(JobOutcome.Failed, message);

    /// <summary>Ends the job. A requested cancel always wins (a cancelled result never shows an error).
    /// Only the first call counts.</summary>
    public void Finish(JobOutcome outcome, string message)
    {
        lock (_lock)
        {
            if (_finished is not null) return;
            if (_cts.IsCancellationRequested) outcome = JobOutcome.Canceled;
            _outcome = outcome;
            if (outcome != JobOutcome.Canceled) { _message = message; _progress = 100; }
            _finished = DateTime.UtcNow;
        }
    }
}

/// <summary>What git/ssh asked the askpass helper.</summary>
public enum PromptKind { UserName, SshPassphrase, OtherPassphrase, SshPassword, Password }

/// <summary>
/// A credentials prompt for the UI. RepoPath may be empty.
/// Target = the key file path (SshPassphrase) or "user@host" (SshPassword), else null. Text = git's prompt.
/// The UI answers the typed text, or null when cancelled.
/// </summary>
public sealed record CredentialPrompt(string RepoPath, PromptKind Kind, string Text, string? Target);

/// <summary>Unknown SSH host key.</summary>
public enum HostKeyAnswer { Cancel, Once, Always }

/// <summary>ssh's full question ("The authenticity of host ... fingerprint is SHA256:... continue connecting"), shown as is.</summary>
public sealed record HostKeyPrompt(string RepoPath, string Text);
