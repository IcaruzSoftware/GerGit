using System.Diagnostics;
using System.Text;

namespace GerGit.Core.Rebase;

public enum RebaseAction { Pick, Edit, Reword, Squash, Fixup, Drop }

/// <summary>Graph glyph of a row.</summary>
public enum RebaseGlyph { None, Start, Middle, Through, Target }

/// <summary>The commit-menu quick actions.</summary>
public enum QuickAction { Reword, Edit, Squash, Fixup, Drop }

/// <summary>One commit line of git's todo (oldest first) with the local branch tips git proposes to update after it.</summary>
public sealed record TodoEntry(RebaseAction Action, string Sha, IReadOnlyList<string> Refs);

/// <summary>
/// One row of the interactive rebase list. Message = the commit's original full message; CustomMessage = typed in the
/// reword popup; GroupMessage = set by the group rules on a squash target. Refs = full names of the branch tips git
/// proposes to update after this commit (the chips).
/// </summary>
public sealed record RebaseRow(RebaseAction Action, string Sha, string AuthorName, string AuthorEmail, DateTimeOffset AuthorDate,
    string Message, IReadOnlyList<string> Refs)
{
    public string? CustomMessage { get; init; }
    public string? GroupMessage { get; init; }
    public RebaseGlyph Glyph { get; init; }

    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;

    /// <summary>What the commit will be called: custom message, else group message, else the original one.</summary>
    public string CurrentMessage => CustomMessage ?? GroupMessage ?? Message;

    public string Subject => CurrentMessage.Split('\n', 2)[0].TrimEnd('\r');

    /// <summary>Text after the first line, leading line breaks removed.</summary>
    public string Description => CurrentMessage.Split('\n', 2) is [_, var rest] ? rest.TrimStart('\r', '\n') : "";
}

/// <summary>
/// The todo list of the interactive rebase window, newest row first. Every change re-runs
/// the group rules. Pure: no git, no UI.
/// </summary>
public sealed class RebaseTodo
{
    List<(RebaseAction Action, string Sha)> _loaded = [];

    public RebaseTodo(IEnumerable<RebaseRow> rows)
    {
        Rows = ApplyGroupRules(rows.ToList());
        MarkLoaded();
    }

    /// <summary>Newest first; replaced (never mutated) on every change.</summary>
    public IReadOnlyList<RebaseRow> Rows { get; private set; }

    /// <summary>Esc and cancel: any row's action or SHA at some index differs from the loaded list.</summary>
    public bool Changed =>
        Rows.Count != _loaded.Count || Rows.Where((r, i) => r.Action != _loaded[i].Action || r.Sha != _loaded[i].Sha).Any();

    /// <summary>The list the cancel confirmation compares against (taken on load and after a quick action's initial action).</summary>
    public void MarkLoaded() => _loaded = Rows.Select(r => (r.Action, r.Sha)).ToList();

    /// <summary>Sets <paramref name="action"/> on the rows at <paramref name="indices"/>. Reword without a message reverts
    /// to pick through the group rules unless the row is a squash target.</summary>
    public void SetAction(IEnumerable<int> indices, RebaseAction action)
    {
        var rows = Rows.ToList();
        foreach (var i in indices.Where(i => i >= 0 && i < rows.Count))
            rows[i] = rows[i] with { Action = action };
        Rows = ApplyGroupRules(rows);
    }

    /// <summary>OK in the reword popup: reword with that message. Empty = cancelled (reverts through the group rules).</summary>
    public void Reword(int index, string? message)
    {
        var rows = Rows.ToList();
        rows[index] = rows[index] with { Action = RebaseAction.Reword, CustomMessage = string.IsNullOrEmpty(message) ? null : message };
        Rows = ApplyGroupRules(rows);
    }

    /// <summary>Ctrl+Up / Move Up: the selected rows as one block at max(first - 1, 0).
    /// Returns the block's new indices (the selection to restore).</summary>
    public IReadOnlyList<int> MoveUp(IEnumerable<int> selected) => Move(selected, (first, _) => Math.Max(first - 1, 0));

    /// <summary>Ctrl+Down / Move Down: the block at min(first + 1, remaining count).</summary>
    public IReadOnlyList<int> MoveDown(IEnumerable<int> selected) => Move(selected, (first, rest) => Math.Min(first + 1, rest.Count));

    /// <summary>Drag and drop: the selected rows (relative order kept) directly before or after the row at
    /// <paramref name="target"/>. Dropping on one of the dragged rows does nothing.</summary>
    public IReadOnlyList<int> MoveTo(IEnumerable<int> selected, int target, bool after)
    {
        var sel = selected.ToHashSet();
        if (sel.Contains(target) || target < 0 || target >= Rows.Count) return sel.Order().ToList();
        var targetRow = Rows[target];
        return Move(sel, (_, rest) => rest.FindIndex(r => ReferenceEquals(r, targetRow)) + (after ? 1 : 0));
    }

    IReadOnlyList<int> Move(IEnumerable<int> selected, Func<int, List<RebaseRow>, int> insertAt)
    {
        var sel = selected.Where(i => i >= 0 && i < Rows.Count).ToHashSet();
        if (sel.Count == 0) return [];
        var block = Rows.Where((_, i) => sel.Contains(i)).ToList();
        var rest = Rows.Where((_, i) => !sel.Contains(i)).ToList();
        int at = insertAt(sel.Min(), rest);
        rest.InsertRange(at, block);
        Rows = ApplyGroupRules(rest);
        return Enumerable.Range(at, block.Count).ToList();
    }

    /// <summary>
    /// The initial action of a quick action (Quick actions table) on the rows of <paramref name="shas"/>; then
    /// the list counts as loaded again (cancelling straight away asks nothing). Returns the row to select (the oldest of
    /// them), null when none is in the list. Reword sets nothing: the caller selects the row and opens the reword popup.
    /// </summary>
    public int? ApplyQuickAction(QuickAction action, IReadOnlyCollection<string> shas)
    {
        var indices = Rows.Select((r, i) => (r, i)).Where(x => shas.Contains(x.r.Sha)).Select(x => x.i).ToList();
        if (action != QuickAction.Reword)
            SetAction(indices, action switch
            {
                QuickAction.Edit => RebaseAction.Edit,
                QuickAction.Squash => RebaseAction.Squash,
                QuickAction.Fixup => RebaseAction.Fixup,
                _ => RebaseAction.Drop,
            });
        MarkLoaded();
        return indices.Count == 0 ? null : indices.Max();
    }

    /// <summary>
    /// Group rules applied in the list on every change. A group starts at a squash/fixup row and ends at the
    /// first row below it that is neither squash, fixup nor drop (its target).
    /// </summary>
    internal static List<RebaseRow> ApplyGroupRules(List<RebaseRow> input)
    {
        // A custom message is dropped whenever the action is not reword.
        var rows = input.Select(r => r with
        {
            Glyph = RebaseGlyph.None,
            GroupMessage = null,
            CustomMessage = r.Action == RebaseAction.Reword ? r.CustomMessage : null,
        }).ToList();
        int i = 0;
        while (i < rows.Count)
        {
            if (rows[i].Action is not (RebaseAction.Squash or RebaseAction.Fixup))
            {
                // Outside any group: reword without a message reverts to pick (how cancelling an empty reword works).
                if (rows[i] is { Action: RebaseAction.Reword, CustomMessage: null }) rows[i] = rows[i] with { Action = RebaseAction.Pick };
                i++;
                continue;
            }
            int j = i;
            bool first = true, squash = false;
            for (; j < rows.Count && rows[j].Action is RebaseAction.Squash or RebaseAction.Fixup or RebaseAction.Drop; j++)
            {
                if (rows[j].Action == RebaseAction.Drop) { rows[j] = rows[j] with { Glyph = RebaseGlyph.Through }; continue; }
                rows[j] = rows[j] with { Glyph = first ? RebaseGlyph.Start : RebaseGlyph.Middle };
                first = false;
                squash |= rows[j].Action == RebaseAction.Squash;
            }
            if (j == rows.Count) break; // the oldest rows meld into nothing: not validated, git fails

            var target = rows[j] with { Glyph = RebaseGlyph.Target };
            if (squash)
            {
                // Target first, then the squash members older to newer; an edit target keeps its own message.
                if (target.Action is RebaseAction.Pick or RebaseAction.Reword)
                {
                    var parts = new List<string> { target.Message };
                    for (int k = j - 1; k >= i; k--)
                        if (rows[k].Action == RebaseAction.Squash) parts.Add(rows[k].Message);
                    target = target with { Action = RebaseAction.Reword, GroupMessage = string.Join("\n\n", parts) };
                }
            }
            else if (target.CustomMessage is null && target.Action != RebaseAction.Edit)
                target = target with { Action = RebaseAction.Pick };
            rows[j] = target;
            i = j + 1;
        }
        return rows;
    }

    /// <summary>
    /// Message archive: commit SHA → the message the commit gets when git asks the editor. Newest first,
    /// dropped rows skipped; squash/fixup rows wait for the next other row and get its message.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Archive(IReadOnlyList<RebaseRow> rows)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        var pending = new List<string>();
        foreach (var r in rows)
        {
            if (r.Action == RebaseAction.Drop) continue;
            if (r.Action is RebaseAction.Squash or RebaseAction.Fixup) { pending.Add(r.Sha); continue; }
            foreach (var p in pending) map[p] = r.CurrentMessage;
            pending.Clear();
            map[r.Sha] = r.CurrentMessage;
        }
        return map;
    }

    /// <summary>Todo list the app writes back: oldest first, `&lt;letter&gt; &lt;sha&gt; &lt;subject&gt;`, then
    /// `update-ref &lt;ref&gt;` per chip when <paramref name="updateRefs"/>.</summary>
    public static string Text(IReadOnlyList<RebaseRow> rows, bool updateRefs)
    {
        var sb = new StringBuilder();
        for (int i = rows.Count - 1; i >= 0; i--)
        {
            var r = rows[i];
            sb.Append(Letter(r.Action)).Append(' ').Append(r.Sha).Append(' ').Append(r.Subject).Append('\n');
            if (updateRefs)
                foreach (var name in r.Refs) sb.Append("update-ref ").Append(name).Append('\n');
        }
        return sb.ToString();
    }

    static char Letter(RebaseAction a) => a switch
    {
        RebaseAction.Pick => 'p',
        RebaseAction.Edit => 'e',
        RebaseAction.Reword => 'r',
        RebaseAction.Squash => 's',
        RebaseAction.Fixup => 'f',
        _ => 'd',
    };

    /// <summary>
    /// Todo list git hands to the editor: commit lines (oldest first) with the SHA after <paramref name="marker"/>,
    /// `u &lt;ref&gt;` lines attached to the commit line before them when the ref is in <paramref name="localBranches"/>
    /// (full names). Blank, `#` and other command lines (`^` comments, noop, label, exec, ...) are ignored.
    /// </summary>
    public static IReadOnlyList<TodoEntry> ParseTodo(string text, string marker, IReadOnlySet<string> localBranches)
    {
        var entries = new List<(RebaseAction Action, string Sha, List<string> Refs)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Trim().Length == 0 || line[0] == '#') continue;
            if (line[0] == 'u')
            {
                int space = line.IndexOf(' ');
                if (space >= 0 && entries.Count > 0 && localBranches.Contains(line[(space + 1)..].Trim()))
                    entries[^1].Refs.Add(line[(space + 1)..].Trim());
                continue;
            }
            RebaseAction? action = line[0] switch
            {
                'p' => RebaseAction.Pick,
                'r' => RebaseAction.Reword,
                'e' => RebaseAction.Edit,
                's' => RebaseAction.Squash,
                'f' => RebaseAction.Fixup,
                'd' => RebaseAction.Drop,
                _ => null,
            };
            if (action is null) continue;
            int m = line.IndexOf(marker, StringComparison.Ordinal);
            if (m < 0 || m + marker.Length + 40 > line.Length)
            {
                Trace.WriteLine($"Interactive rebase: todo line skipped: {line}");
                continue;
            }
            entries.Add((action.Value, line.Substring(m + marker.Length, 40), []));
        }
        return entries.Select(e => new TodoEntry(e.Action, e.Sha, e.Refs)).ToList();
    }

    /// <summary>
    /// Rows (newest first: the last todo line is row 0) from the todo entries and the output of
    /// `git log --no-walk --pretty=format:%H&lt;f&gt;%aN&lt;f&gt;%aE&lt;f&gt;%at&lt;f&gt;%B&lt;e&gt;`. Entries are split at
    /// <paramref name="entrySeparator"/>, trimmed, then split at <paramref name="fieldSeparator"/> (at least 5 fields).
    /// A todo entry without commit data throws GitException: leaving it out would drop that commit from the todo written back.
    /// </summary>
    public static IReadOnlyList<RebaseRow> BuildRows(IReadOnlyList<TodoEntry> entries, string logOutput,
        string fieldSeparator, string entrySeparator)
    {
        var commits = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var entry in logOutput.Split(entrySeparator))
        {
            var f = entry.Trim().Split(fieldSeparator, 5);
            if (f.Length < 5 || !long.TryParse(f[3], out _)) continue;
            commits.TryAdd(f[0], f);
        }
        var rows = new List<RebaseRow>();
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            var e = entries[i];
            if (!commits.TryGetValue(e.Sha, out var f)) throw new GitException($"No commit data for {e.Sha}");
            rows.Add(new RebaseRow(e.Action, e.Sha, f[1], f[2], DateTimeOffset.FromUnixTimeSeconds(long.Parse(f[3])), f[4], e.Refs));
        }
        return rows;
    }

    /// <summary>Commit menu: the selected commits form an unbroken chain where each is the single parent of the one
    /// before it (Where interactive rebase items appear, several commits).</summary>
    public static bool IsParentChain(IReadOnlyCollection<(string Sha, IReadOnlyList<string> Parents)> commits)
    {
        if (commits.Count == 0) return false;
        var parents = commits.ToDictionary(c => c.Sha, c => c.Parents, StringComparer.Ordinal);
        var someonesParent = commits.Where(c => c.Parents.Count == 1).Select(c => c.Parents[0]).ToHashSet(StringComparer.Ordinal);
        var newest = commits.Where(c => !someonesParent.Contains(c.Sha)).ToList();
        if (newest.Count != 1) return false;
        int length = 1;
        for (var sha = newest[0].Sha; parents[sha] is [var p] && parents.ContainsKey(p); sha = p) length++;
        return length == commits.Count;
    }
}
