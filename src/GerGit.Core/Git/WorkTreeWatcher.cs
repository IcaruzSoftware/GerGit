using System.Diagnostics;

namespace GerGit.Core.Git;

/// <summary>
/// "Working-tree file changed": one recursive inotify watcher (one watch per directory) on the work tree.
/// Reports changed paths relative to the work tree, never paths inside a `.git` folder. Symlinked folders are not
/// followed (.NET's watcher does not descend into them). Ignore filtering is done per batch by the owner
/// (<see cref="AnyNotIgnoredAsync"/>), never per event.
/// Start, Stop and Dispose belong to one thread (the UI thread); the events arrive on thread-pool threads (Failed
/// also on the starting thread).
/// Limitation: ignored folders (node_modules, build output) are watched too, only their events are filtered out; their
/// watches use the user's shared inotify budget (other tools can hit the limit), and a tree with more folders than
/// the limit falls back to activation refreshes. Watching only the non-ignored folders needs a direct inotify binding
/// (FileSystemWatcher cannot skip subfolders).
/// </summary>
public sealed class WorkTreeWatcher(string workTree) : IDisposable
{
    readonly string _root = workTree;
    readonly Lock _gate = new(); // a start running on the thread pool and Stop
    volatile FileSystemWatcher? _fsw;
    volatile bool _failed;
    int _folderErrors; // per-folder errors are logged once

    /// <summary>Tests: awaited with true before and false after each `git check-ignore` (no storm of git processes; a
    /// delay here acts as a slow git).</summary>
    internal Func<bool, Task>? CheckIgnoreProbe { get; set; }

    /// <summary>A path changed (relative, '/'-separated); null = events were lost (queue overflow), treat as "anything".</summary>
    public event Action<string?>? Changed;

    /// <summary>The watcher stopped for good (e.g. the OS watch limit): the owner calls <see cref="Stop"/>
    /// and continues without working-tree watching (manual and activation refreshes still work).</summary>
    public event Action<Exception>? Failed;

    /// <summary>True after a failure; <see cref="Start"/> does nothing any more (no retry on every tab switch).</summary>
    public bool IsFailed => _failed;

    public bool IsRunning => _fsw is not null;

    /// <summary>Creates the platform watcher; tests replace it to inject a failure.</summary>
    internal Func<string, FileSystemWatcher> Create { get; set; } = root => new FileSystemWatcher(root);

    public void Start()
    {
        if (_failed || _fsw is not null) return;
        try
        {
            var w = _fsw = Create(_root);
            w.IncludeSubdirectories = true;
            w.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Attributes;
            w.Changed += (_, e) => OnEvent(w, e.FullPath);
            w.Created += (_, e) => OnEvent(w, e.FullPath);
            w.Deleted += (_, e) => OnEvent(w, e.FullPath);
            w.Renamed += (_, e) =>
            {
                OnEvent(w, e.OldFullPath);
                OnEvent(w, e.FullPath);
            };
            w.Error += (_, e) =>
            {
                if (_fsw != w) return; // stopped: its disposal may still be running
                var ex = e.GetException();
                if (ex is InternalBufferOverflowException) { if (!_failed) Changed?.Invoke(null); }
                // ENOSPC: "The configured user limit (8192) on the number of inotify watches has been reached". .NET 10
                // builds it as new IOException(message, errno), so HResult is the errno; the text is the fallback.
                else if (ex is IOException { HResult: ENOSPC } || ex.Message.Contains("number of inotify watches")) Fail(ex);
                // One folder could not be watched (e.g. no read permission): the rest is still watched.
                else if (Interlocked.Increment(ref _folderErrors) == 1)
                    Trace.WriteLine($"Cannot watch a folder in {_root}, continuing: {ex.Message}");
            };
            // Adding one watch per folder takes about 4 ms per 1000 folders: off the UI thread.
            _ = Task.Run(() =>
            {
                lock (_gate)
                {
                    try
                    {
                        if (_fsw == w) w.EnableRaisingEvents = true;
                    }
                    catch (Exception e) { Fail(e); } // the root cannot be watched; the owner stops it
                }
            });
        }
        catch (Exception e) when (IsWatchError(e))
        {
            Fail(e);
            Stop();
        }
    }

    static bool IsWatchError(Exception e) =>
        e is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException;

    const int ENOSPC = 28;

    /// <summary>Stops watching and releases the inotify watches (an inactive tab holds none). The release waits for a
    /// start still adding watches, so it runs on the thread pool; no event is reported once this returns.</summary>
    public void Stop()
    {
        if (_fsw is not { } w) return;
        _fsw = null;
        _ = Task.Run(() =>
        {
            lock (_gate) w.Dispose();
        });
    }

    public void Dispose()
    {
        Stop();
        Changed = null;
        Failed = null;
    }

    void Fail(Exception e)
    {
        if (_failed) return;
        _failed = true;
        Trace.WriteLine($"Cannot watch the working tree {_root}, continuing without: {e.Message}");
        Failed?.Invoke(e);
    }

    void OnEvent(FileSystemWatcher w, string? fullPath)
    {
        if (_failed || _fsw != w || string.IsNullOrEmpty(fullPath)) return; // stopped: its disposal may still be running
        var rel = Path.GetRelativePath(_root, fullPath);
        if (rel == "." || rel == ".." || rel.StartsWith("../", StringComparison.Ordinal)) return; // never outside the work tree
        if (rel.Split('/').Contains(".git")) return; // the repository's (or a nested one's) git data is not a work-tree change
        Changed?.Invoke(rel);
    }

    /// <summary>
    /// True when any of the paths is not ignored by git's rules (all `.gitignore` files, `info/exclude`, the global
    /// ignore file; tracked files never count as ignored). One `git check-ignore --stdin -z` for the whole batch, so
    /// edits of the ignore files apply at once. When git fails (e.g. a path inside a submodule) the answer is true:
    /// one refresh too many is better than a missed change.
    /// </summary>
    public async Task<bool> AnyNotIgnoredAsync(IReadOnlyCollection<string> paths, CancellationToken ct = default)
    {
        if (paths.Count == 0) return false;
        if (CheckIgnoreProbe is { } before) await before(true);
        var r = await GitRunner.RunAsync(_root, ["check-ignore", "--stdin", "-z"], string.Concat(paths.Select(p => p + "\0")), ct);
        if (CheckIgnoreProbe is { } after) await after(false);
        if (r.Cancelled || r.ExitCode is not (0 or 1)) return !r.Cancelled;
        var ignored = r.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        return paths.Any(p => !ignored.Contains(p));
    }
}
