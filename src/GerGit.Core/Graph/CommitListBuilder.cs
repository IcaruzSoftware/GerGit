namespace GerGit.Core.Graph;

/// <summary>
/// Builds commit-list rows from the walk: stash insertion, graph lanes, ref labels,
/// reachability (dimming) and upstream marks. Incremental: Append() extends the rows of the
/// same walk page by page without changing earlier rows. There are no collapsed merges, so the
/// visual parents of a commit are all its parents.
/// </summary>
public sealed class CommitListBuilder(RefSnapshot refs, IReadOnlyList<StashEntry> stashes, bool compactLabels = true)
{
    const int MaxLanes = 200;

    readonly record struct Lane(int Id, string Next);

    readonly List<CommitRow> rows = [];
    readonly Dictionary<string, RefLabel[]> labelsBySha = BuildLabels(refs, compactLabels);
    readonly ILookup<string, StashEntry> stashesByParent = stashes.ToLookup(s => s.ParentSha); // keeps stash@{0} first
    readonly (string Local, string Upstream)? tips = UpstreamTips(refs);

    // Lanes at the bottom edge of the last row; "spare" is reused as the next bottom list.
    List<Lane> lanes = [], spare = [];
    readonly List<GraphLine> lineBuf = [];

    // Commits known to be reachable (from HEAD / the active tip / the upstream tip) but not yet reached
    // in the walk. Rows come children-first, so a row is decided once it is appended.
    readonly HashSet<string> reachable = [], fromLocal = [], fromUpstream = [];

    public IReadOnlyList<CommitRow> Rows => rows;

    /// <summary>Add the next page of walked commits (in walk order) and return the rows added.</summary>
    public IReadOnlyList<CommitRow> Append(IReadOnlyList<WalkedCommit> page)
    {
        int start = rows.Count;
        foreach (var c in page)
        {
            foreach (var s in stashesByParent[c.Sha])
            {
                string[] parent = [s.ParentSha];
                rows.Add(new CommitRow(s.Sha, parent, Layout(s.Sha, parent, false),
                    [new RefLabel(RefLabelKind.Stash, s.Name, s.Name)], false, false, UpstreamMark.None, s));
            }

            bool isReachable = c.Sha == refs.HeadSha | reachable.Remove(c.Sha);
            if (isReachable) reachable.UnionWith(c.Parents);

            var mark = UpstreamMark.None;
            if (tips is { } t)
            {
                bool l = c.Sha == t.Local | fromLocal.Remove(c.Sha);
                bool u = c.Sha == t.Upstream | fromUpstream.Remove(c.Sha);
                if (l) fromLocal.UnionWith(c.Parents);
                if (u) fromUpstream.UnionWith(c.Parents);
                mark = l == u ? UpstreamMark.None : l ? UpstreamMark.Ahead : UpstreamMark.Behind;
            }

            rows.Add(new CommitRow(c.Sha, c.Parents, Layout(c.Sha, c.Parents, c.Parents.Length >= 2),
                labelsBySha.GetValueOrDefault(c.Sha, []), c.Sha == refs.HeadSha, isReachable, mark));
        }
        return rows.GetRange(start, rows.Count - start);
    }

    /// <summary>Places one commit in the lanes: every lane from above passes through, carries the dot or joins it; the
    /// first parent continues in the dot's lane, other parents reuse a lane already waiting for them or get a new one.
    /// Lines are emitted in top-lane order, then new lanes / merge lines.</summary>
    GraphRow Layout(string sha, string[] parents, bool isMerge)
    {
        var top = lanes;
        var bottom = spare;
        bottom.Clear();
        lineBuf.Clear();
        int dot = -1, dotId = 0;

        for (int i = 0; i < top.Count; i++)
        {
            var lane = top[i];
            if (lane.Next != sha)
            {
                lineBuf.Add(new GraphLine(lane.Id, i, bottom.Count)); // waits for another commit: passes through, shifting left over ended lanes
                bottom.Add(lane);
            }
            else if (dot < 0)
            {
                dot = i; // the leftmost lane leading here carries the dot; nothing left of it ended, so bottom column = i
                dotId = lane.Id;
                if (parents.Length > 0)
                {
                    lineBuf.Add(new GraphLine(lane.Id, i, i)); // continues to the first parent
                    bottom.Add(lane with { Next = parents[0] });
                }
                else lineBuf.Add(new GraphLine(lane.Id, i, -1)); // root: lane ends at the dot
            }
            else lineBuf.Add(new GraphLine(lane.Id, i, -1)); // another lane leading here joins the dot
        }

        if (dot < 0)
        {
            if (parents.Length > 0 && bottom.Count < MaxLanes)
            {
                dot = dotId = bottom.Count; // no lane leads here (a branch tip): a new lane on the right for the first parent
                lineBuf.Add(new GraphLine(dotId, -1, dot));
                bottom.Add(new Lane(dotId, parents[0]));
            }
            else dot = 0; // a tip without parents, or no lane left: a plain dot in column 0
        }

        for (int p = 1; p < parents.Length && bottom.Count < MaxLanes; p++) // merge parents: the lane already waiting for the parent, else a new one on the right
        {
            int b = bottom.FindIndex(l => l.Next == parents[p]);
            if (b < 0)
            {
                b = bottom.Count;
                bottom.Add(new Lane(b, parents[p]));
            }
            lineBuf.Add(new GraphLine(bottom[b].Id, -1, b));
        }

        lanes = bottom;
        spare = top;
        return new GraphRow(dot, dotId, isMerge, lineBuf.ToArray());
    }

    static (string, string)? UpstreamTips(RefSnapshot refs)
    {
        var active = refs.Refs.FirstOrDefault(r => r.FullName == refs.ActiveBranch);
        if (active is not { UpstreamState: UpstreamState.Valid, Upstream: { } upName }) return null;
        var upstream = refs.Refs.FirstOrDefault(r => r.FullName == upName);
        return upstream is null || upstream.Sha == active.Sha ? null : (active.Sha, upstream.Sha);
    }

    /// <summary>Order of labels on a row, and compact branch labels.</summary>
    static Dictionary<string, RefLabel[]> BuildLabels(RefSnapshot refs, bool compact)
    {
        var result = new Dictionary<string, RefLabel[]>();
        foreach (var group in refs.Refs.GroupBy(r => r.Sha))
        {
            var sorted = group.Order(Comparer<GitRef>.Create((a, b) => NaturalCompare(a.FullName, b.FullName))).ToList();
            var labels = sorted.Select(r => ToLabel(r, refs.ActiveBranch)).ToList();
            if (compact)
            {
                // The run of local-branch labels at the start of the list"; bisect labels sort first,
                // so read literally they would block compaction. We treat that as ambiguous and compact all locals.
                foreach (var local in sorted.Where(r => r.Kind == RefKind.LocalBranch && r.Upstream is not null))
                {
                    int ri = labels.FindIndex(l => l.Kind == RefLabelKind.RemoteBranch && l.FullName == local.Upstream);
                    if (ri < 0) continue;
                    int li = labels.FindIndex(l => l.FullName == local.FullName);
                    var localLabel = labels[li];
                    labels.RemoveAt(li);
                    if (li < ri) ri--;
                    labels[ri] = labels[ri] with { IconOnly = true, Tooltip = labels[ri].Text };
                    labels.Insert(ri + 1, localLabel);
                }
            }
            result[group.Key] = [.. labels];
        }
        return result;
    }

    static RefLabel ToLabel(GitRef r, string? activeBranch) => r.Kind switch
    {
        RefKind.LocalBranch => new RefLabel(RefLabelKind.LocalBranch, r.ShortName, r.FullName, r.FullName == activeBranch),
        RefKind.RemoteBranch => new RefLabel(RefLabelKind.RemoteBranch, r.ShortName, r.FullName),
        RefKind.Tag => new RefLabel(RefLabelKind.Tag, r.ShortName, r.FullName),
        RefKind.BisectGood => new RefLabel(RefLabelKind.BisectGood, "bisect: " + r.ShortName, r.FullName),
        RefKind.BisectBad => new RefLabel(RefLabelKind.BisectBad, "bisect: " + r.ShortName, r.FullName),
        _ => new RefLabel(RefLabelKind.BisectSkip, "bisect: " + r.ShortName, r.FullName),
    };

    /// <summary>Case-insensitive natural order: digit runs compare as numbers (v2 before v10). Ties fall back to ordinal.</summary>
    internal static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                int ei = i, ej = j;
                while (ei < a.Length && char.IsAsciiDigit(a[ei])) ei++;
                while (ej < b.Length && char.IsAsciiDigit(b[ej])) ej++;
                var da = a.AsSpan(i, ei - i).TrimStart('0');
                var db = b.AsSpan(j, ej - j).TrimStart('0');
                int c = da.Length != db.Length ? da.Length.CompareTo(db.Length) : da.SequenceCompareTo(db);
                if (c != 0) return c;
                i = ei;
                j = ej;
            }
            else
            {
                int c = char.ToUpperInvariant(a[i]).CompareTo(char.ToUpperInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        int rest = (a.Length - i).CompareTo(b.Length - j);
        return rest != 0 ? rest : string.CompareOrdinal(a, b);
    }
}
