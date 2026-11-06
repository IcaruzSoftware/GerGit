using System.Globalization;
using System.Text.RegularExpressions;

namespace GerGit.Core.Git;

/// <summary>The "Local changes:" choice (LeaveAsStash = "Stash and reapply" with Shift held).</summary>
public enum LocalChangesMode { DontChange, StashAndReapply, LeaveAsStash, Discard }

/// <summary>Tag Details window. Date null = the tag object could not be read (Message then holds the
/// for-each-ref fallback text).</summary>
public sealed record TagDetails(string TaggerName, string TaggerEmail, DateTimeOffset? Date, string Message);

/// <summary>Branch, tag, checkout and merge helpers (Branches, "Tags").</summary>
public sealed partial class GitRepository
{
    public const string OverwrittenByCheckout = "error: Your local changes to the following files would be overwritten by checkout:";
    public const string OverwrittenByMerge = "error: Your local changes to the following files would be overwritten by merge:";

    /// <summary>
    /// Autostash algorithm: runs <paramref name="operation"/> (which adds --force itself for Discard).
    /// LeaveAsStash stashes first and never reapplies. StashAndReapply stashes only after the operation failed with
    /// git's "would be overwritten by checkout:" (or "... by merge:", the fast-forward step), redoes it and pops
    /// stash@{0} with the --index retry. Returns the first failing result, else the operation's result.
    /// <paramref name="step"/> gets the footer texts ("Stashing...").
    /// </summary>
    public async Task<GitResult> RunWithAutostashAsync(Job job, LocalChangesMode mode, string stashMessage,
        Func<Task<GitResult>> operation, Action<string>? step = null)
    {
        if (mode == LocalChangesMode.LeaveAsStash)
        {
            step?.Invoke("Stashing...");
            var (s, kept) = await StashPushAsync(job, stashMessage);
            if (!s.Success) return s;
            var op = await operation();
            // Failed or cancelled: the changes left the tree although nothing switched; say where they are.
            if (!op.Success && kept is not null) AppendStashKept(job, stashMessage);
            return op;
        }

        var r = await operation();
        if (mode != LocalChangesMode.StashAndReapply || r.Success || r.Cancelled) return r;
        var line = r.Stderr.Contains(OverwrittenByCheckout, StringComparison.Ordinal)
            ? "failed to checkout without overwriting local changes. Trying again with stash and reapply..."
            : r.Stderr.Contains(OverwrittenByMerge, StringComparison.Ordinal)
                ? "failed to fast-forward overwriting touching local changes. Trying again with stash and reapply..."
                : null;
        if (line is null) return r;

        job.AppendOutput($"{GitErrors.HintPrefix} {line}\n");
        step?.Invoke("Stashing...");
        var (stash, sha) = await StashPushAsync(job, stashMessage);
        if (!stash.Success) return stash;
        // Nothing stashed (e.g. only untracked files in the way): popping stash@{0} would apply an unrelated stash.
        if (sha is null) return r;

        r = await operation();
        // Always reapply, also after a failed or cancelled redo: the user's changes come back either way.
        var applied = await PopAutostashAsync(job, sha, stashMessage);
        return !r.Success ? r : applied ?? r;
    }

    /// <summary>A hint line telling the user where the changes of a stash that was not (fully) reapplied are.</summary>
    public static void AppendStashKept(Job job, string stashMessage) =>
        job.AppendOutput($"{GitErrors.HintPrefix} your local changes are kept in stash@{{0}} '{stashMessage}'\n");

    /// <summary>
    /// Merge conflict preview of the Merge dialog: true = conflicts, false = clean, null = unknown (no merge base,
    /// unrelated histories, any failure). The legacy approach runs merge-base + the legacy 3-argument merge-tree and looks for
    /// conflict markers; `merge-tree --write-tree` (git ≥ 2.38) gives the answer as its exit code (1 = conflicts), also
    /// for conflicts without text markers (modify/delete, rename), and does not mistake marker-like file content.
    /// </summary>
    public async Task<bool?> MergeHasConflictsAsync(string source, string destination, CancellationToken ct = default)
    {
        var r = await GitRunner.RunAsync(Info.WorkTree,
            ["merge-tree", "--write-tree", "--name-only", "--no-messages", destination, source], ct: ct);
        return r.Cancelled ? null : r.ExitCode switch { 0 => false, 1 => true, _ => null };
    }

    /// <summary>Tag Details window data: the tag object, else the for-each-ref text as the message.</summary>
    public async Task<TagDetails> ReadTagDetailsAsync(GitRef tag, CancellationToken ct = default)
    {
        if (tag.TagObjectSha is { } obj)
        {
            var r = await GitRunner.RunAsync(Info.WorkTree, ["cat-file", "tag", obj], ct: ct);
            if (r.Success && ParseTagObject(r.Stdout) is { } details) return details;
        }
        var f = await GitRunner.RunAsync(Info.WorkTree, ["for-each-ref",
            "--format=%(taggername) %(taggeremail) %(taggerdate)%0a%0a%(contents)", tag.FullName], ct: ct);
        return new TagDetails("", "", null, f.Stdout.Trim());
    }

    /// <summary>Headers up to the first blank line ("tagger Name &lt;mail&gt; 1700000000 +0100"), then the message.</summary>
    internal static TagDetails? ParseTagObject(string text)
    {
        int blank = text.IndexOf("\n\n", StringComparison.Ordinal);
        var headers = (blank < 0 ? text : text[..blank]).Split('\n');
        var message = blank < 0 ? "" : text[(blank + 2)..].TrimEnd('\n');
        foreach (var h in headers)
        {
            var m = TaggerRegex().Match(h);
            if (!m.Success || !long.TryParse(m.Groups[3].Value, out var seconds)) continue;
            var tz = m.Groups[4].Value;
            var offset = new TimeSpan(int.Parse(tz[1..3], CultureInfo.InvariantCulture), int.Parse(tz[3..5], CultureInfo.InvariantCulture), 0);
            if (tz[0] == '-') offset = -offset;
            var date = DateTimeOffset.FromUnixTimeSeconds(seconds).ToOffset(offset);
            return new TagDetails(m.Groups[1].Value, m.Groups[2].Value, date, message);
        }
        return null;
    }

    [GeneratedRegex(@"^tagger (.*) <(.*)> (\d+) ([+-]\d{4})$")]
    private static partial Regex TaggerRegex();
}
