using System.Globalization;
using GerGit.Core.Rebase;

namespace GerGit.Core.Git;

/// <summary>How a rebase ended. Error = what the error dialog shows (git's output, a
/// loading error or a failed submodule update); null = no dialog. Success with an Error = only the submodule update failed.</summary>
public sealed record RebaseOutcome(bool Success, GitResult? Error);

/// <summary>
/// One interactive rebase, independent of the window that drives it: <see cref="StartAsync"/>
/// starts git, which stays blocked in the editor helper until <see cref="ApplyAsync"/> or <see cref="Cancel"/> answers.
/// <see cref="Completion"/> ends when git has exited. <paramref name="branch"/> = the branch to rebase (checked out first
/// when needed), <paramref name="target"/> = the target object name, null = `--root`.
/// </summary>
public sealed class InteractiveRebaseSession(GitRepository repo, string branch, string? target)
{
    /// <summary>Marks the full SHA in git's todo lines (rebase.instructionFormat).</summary>
    internal const string Marker = "~>";
    const string Field = "\x1f", Entry = "\0";

    readonly TaskCompletionSource<string> _announced = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<string> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource<RebaseOutcome> _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly object _gate = new();
    bool _applying; // ApplyAsync owns the answer: neither the timeout nor Cancel may answer while it writes
    string? _loadError;

    public string Branch => branch;
    public string? Target => target;

    /// <summary>How long git may wait for the user; then the helper is answered `cancel`.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>Ends when git has exited (also when it never started).</summary>
    public Task<RebaseOutcome> Completion => _done.Task;

    /// <summary>git waits in the helper for an answer.</summary>
    public bool Waiting => _announced.Task.IsCompleted && !_answer.Task.IsCompleted && !_done.Task.IsCompleted;

    /// <summary>
    /// Checks out <see cref="Branch"/> if needed, starts git as a step of <paramref name="job"/> and waits for the todo.
    /// Returns the loaded list (empty for git's `noop`), or null when there is none to show: git ended before (pre-flight
    /// error, cancel) or loading failed; <see cref="Completion"/> has the result then. Job cancel = <see cref="Cancel"/>.
    /// Throws when a step before git starts throws (e.g. GitException from the submodule capture); git never ran then
    /// and <see cref="Completion"/> is faulted with the same exception.
    /// </summary>
    public async Task<RebaseTodo?> StartAsync(Job job)
    {
        var workTree = repo.Info.WorkTree;
        var editor = RebaseEditor.ConfigArgs();
        if (editor.Count == 0) return End(new(false, Failed("The rebase editor helper of GerGit is not running.")));

        // Anything thrown before git runs (a failing `git status` in the submodule capture, ...) must still end
        // Completion, else a caller awaiting it hangs: Completion faults with the same exception, which is rethrown.
        bool registered = false;
        try
        {
            // Opening the window anyway after a failed checkout, which would rebase the wrong branch.
            var head = await GitRunner.RunAsync(workTree, ["symbolic-ref", "-q", "HEAD"]);
            if (head.Stdout.Trim() != "refs/heads/" + branch)
            {
                var checkout = GitRepository.RefuseOptionLikeBranch(branch) ?? await GitRunner.RunJobAsync(job, workTree, ["checkout", "--progress", branch]);
                if (!checkout.Success) return End(new(false, checkout.Cancelled ? null : checkout));
            }

            var submodules = await repo.CaptureSubmodulesAsync();
            if (!RebaseEditor.Register(workTree, OnTodoAsync))
                return End(new(false, Failed("Another interactive rebase of this repository is waiting.")));
            registered = true;
            List<string> args = ["-c", "core.commentChar=^", "-c", $"rebase.instructionFormat={Marker}%H",
                "-c", "rebase.abbreviateCommands=true", .. editor, "rebase", "-i", "--autosquash", "--update-refs", target ?? "--root"];
            _ = RunGitAsync(job, args, submodules);
        }
        catch (Exception e)
        {
            if (registered) RebaseEditor.Unregister(workTree, OnTodoAsync);
            _done.TrySetException(e);
            throw;
        }

        await Task.WhenAny(_announced.Task, _done.Task);
        if (!Waiting) return null;
        try
        {
            return new RebaseTodo(await LoadAsync(await _announced.Task));
        }
        catch (Exception e) when (e is GitException or IOException or UnauthorizedAccessException)
        {
            _loadError = e.Message;
            Cancel();
            return null;
        }
    }

    /// <summary>`Rebase`: claims the answer (the timeout and <see cref="Cancel"/> wait meanwhile), writes the todo (oldest
    /// first) and the message archive, creates the backup branch if asked (failure ignored), then answers `start`. Does
    /// nothing when git is not waiting any more. Throws IOException / UnauthorizedAccessException when the todo or the
    /// archive cannot be written; git is answered `cancel` then and <see cref="Completion"/> still ends.</summary>
    public async Task ApplyAsync(IReadOnlyList<RebaseRow> rows, bool updateRefs, bool backup)
    {
        lock (_gate)
        {
            if (!Waiting || _applying) return;
            _applying = true;
        }
        // Every path from here answers git: "cancel" unless the todo and the archive were written (no half-written todo).
        var answer = "cancel";
        try
        {
            var todoPath = await _announced.Task;
            await File.WriteAllTextAsync(todoPath, RebaseTodo.Text(rows, updateRefs));
            RebaseEditor.WriteArchive(Path.GetDirectoryName(todoPath)!, RebaseTodo.Archive(rows));
            answer = "start";
            if (backup)
                try
                {
                    await GitRunner.RunAsync(repo.Info.WorkTree, ["branch", "--no-track",
                        $"backup/{branch}-{DateTime.Now.ToString("HH-mm-ss", CultureInfo.InvariantCulture)}", "HEAD"]);
                }
                catch (Exception e) { System.Diagnostics.Trace.WriteLine($"Backup branch failed: {e.Message}"); } // ignored
        }
        finally
        {
            _answer.TrySetResult(answer);
        }
    }

    /// <summary>Answers `cancel`: the helper empties the todo and git ends with "nothing to do" (a success). Ignored while
    /// <see cref="ApplyAsync"/> is answering.</summary>
    public void Cancel()
    {
        lock (_gate)
            if (!_applying) _answer.TrySetResult("cancel");
    }

    RebaseTodo? End(RebaseOutcome outcome)
    {
        _done.TrySetResult(outcome);
        return null;
    }

    static GitResult Failed(string text) => new(1, "", text);

    /// <summary>The helper's request: wait for the user, at most <see cref="Timeout"/> (then cancel).</summary>
    async Task<string> OnTodoAsync(string todoPath)
    {
        _announced.TrySetResult(todoPath);
        await Task.WhenAny(_answer.Task, Task.Delay(Timeout));
        Cancel();
        return await _answer.Task;
    }

    async Task RunGitAsync(Job job, List<string> args, SubmoduleCapture submodules)
    {
        var workTree = repo.Info.WorkTree;
        try
        {
            GitResult r;
            // Not cancellable through the job: a SIGINT would hit the helper; a cancel answers it instead. Once the
            // todo is accepted git runs to its end or its next stop.
            // Limitation: a hanging hook after `start` cannot be cancelled.
            using (job.Token.Register(Cancel))
            {
                try { r = await GitRunner.RunJobAsync(job, workTree, args, ignoreCancel: true, editor: true); }
                finally
                {
                    RebaseEditor.Unregister(workTree, OnTodoAsync);
                    Cancel(); // nobody waits any more
                }
            }
            _done.TrySetResult(await InterpretAsync(job, r, submodules));
        }
        catch (Exception e)
        {
            _done.TrySetException(e);
        }
    }

    async Task<RebaseOutcome> InterpretAsync(Job job, GitResult r, SubmoduleCapture submodules)
    {
        if (_loadError is not null) return new(false, Failed(_loadError));
        if (r.Success)
        {
            var sub = await repo.UpdateSubmodulesAsync(job, submodules);
            return new(true, sub is { Success: false } ? sub : null);
        }
        bool Has(string text) => r.Stderr.Contains(text, StringComparison.OrdinalIgnoreCase);
        if (Has("nothing to do") || Has("error: Failed to merge in the changes.") ||
            Has("Resolve all conflicts manually, mark them as resolved with"))
            return new(true, null);
        // The helper died (app shutting down): no dialog. git 2.53 says "there was a problem with the editor".
        if (Has("Could not execute editor") || Has("there was a problem with the editor")) return new(false, null);
        return new(false, r);
    }

    async Task<IReadOnlyList<RebaseRow>> LoadAsync(string todoPath)
    {
        var workTree = repo.Info.WorkTree;
        var text = await File.ReadAllTextAsync(todoPath);
        var heads = await GitRunner.RunAsync(workTree, ["for-each-ref", "--format=%(refname)", "refs/heads/"]);
        if (!heads.Success) throw new GitException(heads.ReadableOutput);
        var entries = RebaseTodo.ParseTodo(text, Marker,
            heads.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal));
        if (entries.Count == 0) return [];
        var log = await GitRunner.RunAsync(workTree, ["log", "--no-show-signature", "--no-walk",
            "--pretty=format:%H%x1f%aN%x1f%aE%x1f%at%x1f%B%x00", "--stdin"],
            string.Join('\n', entries.Select(e => e.Sha).Distinct()) + "\n");
        if (!log.Success) throw new GitException(log.ReadableOutput);
        return RebaseTodo.BuildRows(entries, log.Stdout, Field, Entry);
    }
}
