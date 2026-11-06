using System.Globalization;

namespace GerGit.Core.Git;

/// <summary>Result of applying a stash (<see cref="GitRepository.ApplyStashAsync"/>). Conflict = git reported a merge
/// conflict, shown in the "Merge Conflict" error window.</summary>
public sealed record StashApplyResult(GitResult Result, bool Conflict);

/// <summary>Submodules before an operation that moves HEAD or the working tree; Changed = paths shown as changed then.</summary>
public sealed record SubmoduleCapture(IReadOnlyList<SubmoduleEntry> Submodules, IReadOnlySet<string> Changed);

/// <summary>Building blocks shared by the operations.</summary>
public sealed partial class GitRepository
{
    /// <summary>Preference "Update submodules automatically after checkout, pull and merge" (default on). Only
    /// submodules the user initialized are updated, see <see cref="UpdateSubmodulesAsync"/>.</summary>
    public static bool UpdateSubmodulesOnCheckout { get; set; } = true;

    /// <summary>`git checkout` and `git worktree add` have no way to take a branch whose name starts with "-": git reads
    /// it as an option (`--` makes checkout take a path, and worktree add then detaches HEAD). Such a branch can come
    /// from a clone, for example a remote HEAD named like that. The failed result to show, else null.</summary>
    public static GitResult? RefuseOptionLikeBranch(string branch) => branch.StartsWith('-')
        ? new GitResult(1, "", $"The branch name '{branch}' starts with '-', which git would read as an option. Rename the branch first.")
        : null;

    /// <summary>Local-changes block: a staged non-submodule change, or an unstaged non-submodule change of a
    /// tracked file. Untracked and ignored files do not count.</summary>
    public static bool IsDirty(StatusSnapshot s) =>
        s.Staged.Any(e => !e.IsSubmodule) ||
        s.Unstaged.Any(e => !e.IsSubmodule && e.Kind is not (ChangeKind.Untracked or ChangeKind.Ignored));

    /// <summary>The &lt;timestamp&gt; of autostash and pull stash messages: local time.</summary>
    public static string StashTimestamp() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public const string StashListChanged = "Stash list changed. Refresh and try again.";

    /// <summary>Stash tracked changes (`push`, not the legacy `save`). Not cancellable, like
    /// the apply: a cancel would leave the changes only in the stash (or a half-reset tree); the caller's next step then
    /// sees the cancel and reapplies. Sha = the new stash commit, null when nothing was stashed ("No local changes to
    /// save" is exit 0) or the push failed.
    /// Limitation: a stash pushed by another job between the push and the rev-parse is taken for ours; `stash create` +
    /// `stash store` + reset would close that window.</summary>
    public async Task<(GitResult Result, string? Sha)> StashPushAsync(Job job, string message)
    {
        var r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["stash", "push", "--message", message], ignoreCancel: true);
        if (!r.Success || r.Stdout.Contains("No local changes to save", StringComparison.Ordinal)) return (r, null);
        return (r, (await GitRunner.RunAsync(Info.WorkTree, ["rev-parse", "--verify", "-q", "refs/stash"])).Stdout.Trim());
    }

    /// <summary>Null when <paramref name="stashRef"/> ("refs/stash@{n}") still is the stash commit <paramref name="sha"/>,
    /// else a failed result with <see cref="StashListChanged"/>: jobs run in parallel, so another job's
    /// stash push or drop may have shifted the index the UI showed.</summary>
    async Task<GitResult?> CheckStashAsync(string stashRef, string sha)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree, ["rev-parse", "--verify", "-q", stashRef]);
        return r.Success && r.Stdout.Trim() == sha ? null : new GitResult(1, "", StashListChanged);
    }

    /// <summary>Applies or pops a stash, keeping the staged state (`--index`) and retrying without it when git asks to.
    /// <paramref name="reflogName"/> e.g. "stash@{0}", <paramref name="sha"/> the
    /// stash commit it must still be (else nothing runs, <see cref="CheckStashAsync"/>). Runs even when the job was
    /// cancelled, and is not cancellable: a half-applied stash is worse than waiting.</summary>
    public async Task<StashApplyResult> ApplyStashAsync(Job job, string reflogName, string sha, bool pop)
    {
        var stashRef = reflogName.StartsWith("stash", StringComparison.Ordinal) ? "refs/" + reflogName : reflogName;
        if (await CheckStashAsync(stashRef, sha) is { } moved) return new StashApplyResult(moved, false);
        var verb = pop ? "pop" : "apply";
        var r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["stash", verb, "--index", stashRef], ignoreCancel: true);
        if (!r.Success && r.Stderr.Contains("conflicts in index. Try without --index.", StringComparison.Ordinal))
            r = await GitRunner.RunJobAsync(job, Info.WorkTree, ["stash", verb, stashRef], ignoreCancel: true);
        return new StashApplyResult(r, r.Stdout.Contains("CONFLICT (", StringComparison.Ordinal)); // any conflict kind, as GitErrors
    }

    /// <summary>
    /// Reapplies an autostash (pull, checkout, track, checkout and sync) with <see cref="ApplyStashAsync"/>, also after a cancel.
    /// Not while a rebase, merge, cherry-pick or revert is in progress (a cancel can leave one): the pop
    /// would land on the half-done state and a later abort (`reset --hard`) would lose the changes with the stash gone.
    /// Null = not popped. Not popped or failed: the hint line says where the changes are.
    /// </summary>
    public async Task<GitResult?> PopAutostashAsync(Job job, string sha, string stashMessage)
    {
        string[] inProgress = ["rebase-merge", "rebase-apply", "MERGE_HEAD", "CHERRY_PICK_HEAD", "REVERT_HEAD"];
        if (inProgress.Any(n => Path.Exists(Path.Combine(Info.GitDir, n))))
        {
            AppendStashKept(job, stashMessage);
            return null;
        }
        var r = (await ApplyStashAsync(job, "stash@{0}", sha, pop: true)).Result;
        if (!r.Success) AppendStashKept(job, stashMessage);
        return r;
    }

    /// <summary>Submodule refresh after switching commits, first half: call before the operation.</summary>
    public async Task<SubmoduleCapture> CaptureSubmodulesAsync(CancellationToken ct = default)
    {
        var subs = await ReadSubmodulesAsync(ct);
        if (subs.Count == 0) return new SubmoduleCapture(subs, new HashSet<string>());
        var status = await ReadStatusAsync(lockFree: true, ct);
        var changed = status.Unstaged.Concat(status.Staged).Where(e => e.IsSubmodule).Select(e => e.Path)
            .ToHashSet(StringComparer.Ordinal);
        return new SubmoduleCapture(subs, changed);
    }

    /// <summary>Second half, after the operation (also after a failed one): updates previously known active submodules that
    /// were not shown as changed and still exist, plus newly appeared active ones, but only those that pass
    /// <see cref="InitializedAsync"/>. Never `--init`, and GerGit's pulls run with `--no-recurse-submodules`, so a
    /// submodule that a new commit adds to .gitmodules is never initialized or cloned here. Null = nothing ran
    /// (preference off, no submodules before, nothing qualifies, or the job was cancelled).</summary>
    public async Task<GitResult?> UpdateSubmodulesAsync(Job job, SubmoduleCapture before)
    {
        if (!UpdateSubmodulesOnCheckout || before.Submodules.Count == 0 || job.CancelRequested) return null;
        var known = before.Submodules.DistinctBy(s => s.Path).ToDictionary(s => s.Path, StringComparer.Ordinal);
        var paths = await InitializedAsync(Info.WorkTree, (await ReadSubmodulesAsync())
            .Where(s => known.TryGetValue(s.Path, out var old) ? old.Active && !before.Changed.Contains(s.Path) : s.Active));
        if (paths.Count == 0) return null;
        job.SetMessage("Updating submodules...");
        return await UpdateInitializedAsync(job, null, paths, 1);
    }

    /// <summary>Nested submodules deeper than this are not updated.</summary>
    const int MaxSubmoduleDepth = 16;

    /// <summary>
    /// The paths of <paramref name="subs"/> that are safe to update without `--init`, each one:
    /// - listed once in .gitmodules: its name has exactly one `path` and no other name claims that path;
    /// - exactly a submodule entry (mode 160000) of the index at that path, so no pathspec magic and no symlink;
    /// - `submodule.&lt;name&gt;.url` in the local config (included files count), i.e. initialized by the user;
    /// - checked out: `&lt;path&gt;/.git` exists, reached through real folders inside <paramref name="workTree"/> only.
    /// A checked-out submodule is only fetched from its own remote, never cloned. A pathspec in `submodule.active`
    /// (written by `clone --recurse-submodules`) is not enough: git would clone any submodule the upstream lists.
    /// </summary>
    static async Task<List<string>> InitializedAsync(string workTree, IEnumerable<SubmoduleEntry> subs)
    {
        var wanted = subs.ToList();
        if (wanted.Count == 0) return [];
        var listed = await UnambiguousSubmodulesAsync(workTree);
        var config = await GitRunner.RunAsync(workTree, ["config", "--local", "--includes", "--null", "--list"]);
        if (!config.Success) return [];
        var urls = ParseConfig(config.Stdout).Where(c => c is { Section: "submodule", Key: "url", Subsection: not null })
            .Select(c => c.Subsection!).ToHashSet(StringComparer.Ordinal);
        // The checks without git first: a path git rejects as a pathspec ("../x", "/etc") must not fail the index read
        // for the others.
        var paths = wanted
            .Where(s => listed.GetValueOrDefault(s.Name) == s.Path && urls.Contains(s.Name) && IsCheckedOutInside(workTree, s.Path))
            .Select(s => s.Path).Distinct().ToList();
        if (paths.Count == 0) return [];
        var gitlinks = await GitlinksAsync(workTree, paths);
        return paths.Where(gitlinks.Contains).ToList();
    }

    /// <summary>Those of <paramref name="paths"/> that are exactly a submodule entry (mode 160000) of the index of
    /// <paramref name="workTree"/>; empty when git fails.</summary>
    static async Task<HashSet<string>> GitlinksAsync(string workTree, IReadOnlyList<string> paths)
    {
        var index = await GitRunner.RunAsync(workTree, ["--literal-pathspecs", "ls-files", "--stage", "-z", "--", .. paths]);
        if (!index.Success) return [];
        // `<mode> <sha> <stage>\t<path>`
        return index.Stdout.Split('\0').Where(e => e.StartsWith("160000 ", StringComparison.Ordinal) && e.Contains('\t'))
            .Select(e => e[(e.IndexOf('\t') + 1)..]).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>For "Initialize Submodules...": the submodules that are not checked out yet, as (path, URL from
    /// .gitmodules), sorted by path. Only entries listed once in .gitmodules, with a plain relative path that is exactly a
    /// submodule entry of the index, so the confirmation shows everything `submodule update --init` will clone at the
    /// top level. Url = the one git will use: the local `submodule.&lt;name&gt;.url` when set, else the one in .gitmodules.
    /// RelativeTo = for a relative URL ("./", "../"), the URL of the remote git resolves it against (the current branch's
    /// remote, else origin), or "this repository" when there is none; null for an absolute URL.</summary>
    public async Task<IReadOnlyList<(string Path, string Url, string? RelativeTo)>> ReadUninitializedSubmodulesAsync()
    {
        var listed = await UnambiguousSubmodulesAsync(Info.WorkTree);
        if (listed.Count == 0) return [];
        var r = await GitRunner.RunAsync(Info.WorkTree, ["config", "-f", Path.Combine(Info.WorkTree, ".gitmodules"), "--null", "--list"]);
        var urls = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in ParseConfig(r.Stdout))
            if (c is { Section: "submodule", Key: "url", Subsection: { } name, Value: { Length: > 0 } url }) urls.TryAdd(name, url);
        var local = ParseConfig((await GitRunner.RunAsync(Info.WorkTree, ["config", "--local", "--includes", "--null", "--list"])).Stdout)
            .ToList();
        foreach (var c in local)
            if (c is { Section: "submodule", Key: "url", Subsection: { } name, Value: { Length: > 0 } url } && urls.ContainsKey(name))
                urls[name] = url;
        var head = (await GitRunner.RunAsync(Info.WorkTree, ["symbolic-ref", "-q", "--short", "HEAD"])).Stdout.Trim();
        string Value(string section, string sub, string key) =>
            local.LastOrDefault(c => c.Section == section && c.Subsection == sub && c.Key == key).Value ?? "";
        var remote = Value("branch", head, "remote") is { Length: > 0 } r2 ? r2 : "origin";
        var relativeTo = Value("remote", remote, "url") is { Length: > 0 } remoteUrl ? remoteUrl : "this repository";
        var candidates = listed
            .Where(kv => urls.ContainsKey(kv.Key) && kv.Value.Split('/').All(p => p is not ("" or "." or ".."))
                && !Path.Exists(Path.Combine(Info.WorkTree, kv.Value, ".git")))
            .ToList();
        if (candidates.Count == 0) return [];
        var gitlinks = await GitlinksAsync(Info.WorkTree, candidates.Select(kv => kv.Value).ToList());
        return candidates.Where(kv => gitlinks.Contains(kv.Value)).Select(kv => (Path: kv.Value, Url: urls[kv.Key],
                RelativeTo: urls[kv.Key].StartsWith("./", StringComparison.Ordinal) || urls[kv.Key].StartsWith("../", StringComparison.Ordinal)
                    ? relativeTo : null))
            .OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>"Initialize Submodules...", an explicit user action after the confirmation: clones <paramref name="paths"/>
    /// and, recursively, the submodules inside them.</summary>
    public Task<GitResult> InitializeSubmodulesAsync(Job job, IReadOnlyList<string> paths) =>
        GitRunner.RunJobAsync(job, Info.WorkTree,
            ["--literal-pathspecs", "submodule", "update", "--init", "--recursive", "--progress", "--", .. paths]);

    /// <summary>Name → path of the .gitmodules entries of <paramref name="workTree"/> that are not ambiguous: a name with
    /// several `path` values, or a path claimed by several names, is left out.</summary>
    static async Task<Dictionary<string, string>> UnambiguousSubmodulesAsync(string workTree)
    {
        var file = Path.Combine(workTree, ".gitmodules");
        if (!File.Exists(file)) return [];
        var r = await GitRunner.RunAsync(workTree, ["config", "-f", file, "--null", "--list"]);
        if (!r.Success) return [];
        var paths = ParseConfig(r.Stdout).Where(c => c is { Section: "submodule", Key: "path", Subsection: not null, Value: not null })
            .GroupBy(c => c.Subsection!, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Value!, StringComparer.Ordinal);
        var claimed = paths.Values.GroupBy(p => p, StringComparer.Ordinal).Where(g => g.Count() > 1).Select(g => g.Key).ToHashSet();
        return paths.Where(kv => !claimed.Contains(kv.Value)).ToDictionary(StringComparer.Ordinal);
    }

    /// <summary><paramref name="workTree"/>/<paramref name="path"/> is reached through real folders only (no symlink, no
    /// "." or ".."), so it lies inside the work tree, and has a `.git` (the submodule is checked out).</summary>
    static bool IsCheckedOutInside(string workTree, string path)
    {
        var dir = workTree;
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or "..") return false;
            dir = Path.Combine(dir, part);
            if (new DirectoryInfo(dir) is not { Exists: true, LinkTarget: null }) return false;
        }
        return Path.Exists(Path.Combine(dir, ".git"));
    }

    /// <summary>`submodule update` (no `--init`, literal paths) of <paramref name="paths"/> in the submodule at
    /// <paramref name="dir"/> (relative to the work tree, null = the repository itself), then the same for their
    /// submodules that pass <see cref="InitializedAsync"/>, down to <see cref="MaxSubmoduleDepth"/> levels.
    /// Not `--recursive`: git would update every nested submodule that a `submodule.active` pathspec marks as active,
    /// including ones the new commit only just added.</summary>
    async Task<GitResult> UpdateInitializedAsync(Job job, string? dir, IReadOnlyList<string> paths, int depth)
    {
        var r = await GitRunner.RunJobAsync(job, Info.WorkTree,
            ["--literal-pathspecs", .. dir is null ? [] : new[] { "-C", dir }, "submodule", "update", "--progress", "--", .. paths]);
        if (depth >= MaxSubmoduleDepth) return r;
        foreach (var path in paths)
        {
            if (!r.Success || job.CancelRequested) break;
            var sub = dir is null ? path : dir + "/" + path;
            if (!IsCheckedOutInside(Info.WorkTree, sub)) continue;
            var full = Path.Combine(Info.WorkTree, sub);
            var nested = await InitializedAsync(full, (await ReadSubmodulesAsync(full, default)).Where(s => s.Active));
            if (nested.Count > 0) r = await UpdateInitializedAsync(job, sub, nested, depth + 1);
        }
        return r;
    }
}
