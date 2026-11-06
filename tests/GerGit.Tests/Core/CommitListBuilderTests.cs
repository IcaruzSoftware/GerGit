using System.Diagnostics;
using GerGit.Core;
using GerGit.Core.Graph;
using static GerGit.Tests.Core.GraphTests;

namespace GerGit.Tests.Core;

/// <summary>Stashes, labels, reachability, upstream marks, paging.</summary>
public class CommitListBuilderTests
{
    static readonly DateTimeOffset Date = DateTimeOffset.UnixEpoch;

    static GitRef Local(string name, string sha, string? upstream = null, UpstreamState state = UpstreamState.Valid) =>
        new($"refs/heads/{name}", sha, RefKind.LocalBranch, Date, Upstream: upstream, UpstreamState: upstream is null ? UpstreamState.None : state);

    static GitRef Remote(string name, string sha) => new($"refs/remotes/{name}", sha, RefKind.RemoteBranch, Date);

    static StashEntry Stash(int n, string sha, string parent) => new(sha, parent, "a", "a@x", Date, $"stash@{{{n}}}", "On main: x", "x");

    static string Dump(CommitRow r) =>
        $"{r.Sha} {string.Join(',', r.VisualParents)} {r.Graph.DotColumn}/{r.Graph.DotLaneId}/{r.Graph.IsMerge} " +
        $"[{string.Join(' ', r.Graph.Lines)}] [{string.Join(' ', r.Labels)}] {r.IsHead} {r.IsReachable} {r.Upstream} {r.Stash?.Name}";

    [Fact]
    public void StashRowsGoAboveTheirParentInStashOrder()
    {
        var builder = new CommitListBuilder(new RefSnapshot([], "A", null),
            [Stash(0, "S0", "B"), Stash(1, "S1", "B"), Stash(2, "S2", "missing")]);
        var r = builder.Append([C("A", "B"), C("B")]);

        Assert.Equal(["A", "S0", "S1", "B"], r.Select(x => x.Sha));
        Assert.Equal("stash@{0}", Assert.Single(r[1].Labels).Text);
        Assert.Equal(RefLabelKind.Stash, r[1].Labels[0].Kind);
        Assert.Equal(["B"], r[1].VisualParents);
        Assert.False(r[1].IsReachable);
        Assert.False(r[1].Graph.IsMerge);
        // Stash rows are tips: S0 gets lane 1, S1 lane 2, both join B's dot.
        Assert.Equal(1, r[1].Graph.DotColumn);
        Assert.Equal(2, r[2].Graph.DotColumn);
        Assert.Equal([new GraphLine(0, 0, -1), new GraphLine(1, 1, -1), new GraphLine(2, 2, -1)], r[3].Graph.Lines);
        Assert.True(r[3].IsReachable);
    }

    [Fact]
    public void LabelsAreNaturallySortedByFullName()
    {
        // Each pair sorts the other way round ordinally (v10 < v9, Beta < alpha).
        var refs = new RefSnapshot([
            new GitRef("refs/tags/v10", "X", RefKind.Tag, Date),
            new GitRef("refs/tags/v9", "X", RefKind.Tag, Date),
            Remote("origin/main", "X"),
            Local("Beta", "X"),
            Local("alpha", "X"),
            new GitRef("refs/bisect/bad", "X", RefKind.BisectBad, Date),
        ], "X", "refs/heads/alpha");
        var row = new CommitListBuilder(refs, [], compactLabels: false).Append([C("X")])[0];

        Assert.Equal(["bisect: bad", "alpha", "Beta", "origin/main", "v9", "v10"], row.Labels.Select(l => l.Text));
        Assert.True(row.Labels.Single(l => l.Text == "alpha").IsActive);
        Assert.False(row.Labels.Single(l => l.Text == "Beta").IsActive);
        Assert.True(row.IsHead);
    }

    [Theory]
    [InlineData("v9", "v10")]
    [InlineData("alpha", "Beta")]
    [InlineData("v02", "v2")] // equal numbers: ordinal tie-break
    [InlineData("refs/heads/a2b", "refs/heads/a10a")]
    public void NaturalCompareOrders(string first, string second)
    {
        Assert.True(CommitListBuilder.NaturalCompare(first, second) < 0);
        Assert.True(CommitListBuilder.NaturalCompare(second, first) > 0);
    }

    [Fact]
    public void CompactLabelsMoveLocalBehindItsUpstream()
    {
        var refs = new RefSnapshot([
            Local("feature", "X"),
            Local("main", "X", "refs/remotes/origin/main"),
            Remote("origin/main", "X"),
        ], "X", "refs/heads/main");
        var labels = new CommitListBuilder(refs, []).Append([C("X")])[0].Labels;

        Assert.Equal(["feature", "origin/main", "main"], labels.Select(l => l.Text));
        Assert.False(labels[0].IconOnly);
        Assert.True(labels[1].IconOnly);
        Assert.Equal("origin/main", labels[1].Tooltip);
        Assert.False(labels[2].IconOnly);
    }

    // Diverged history: main = L2 -> L1 -> B, origin/main = U1 -> B, other = X -> R, B -> R.
    static readonly WalkedCommit[] Diverged = [C("L2", "L1"), C("U1", "B"), C("X", "R"), C("L1", "B"), C("B", "R"), C("R")];

    static IReadOnlyList<CommitRow> BuildDiverged(UpstreamState state, string upstreamSha = "U1") =>
        new CommitListBuilder(new RefSnapshot([
            Local("main", "L2", "refs/remotes/origin/main", state),
            Remote("origin/main", upstreamSha),
            Local("other", "X"),
        ], "L2", "refs/heads/main"), []).Append(Diverged);

    [Fact]
    public void ReachabilityAndUpstreamMarksOnDivergedHistory()
    {
        var r = BuildDiverged(UpstreamState.Valid);
        Assert.Equal([true, false, false, true, true, true], r.Select(x => x.IsReachable));
        Assert.Equal([UpstreamMark.Ahead, UpstreamMark.Behind, UpstreamMark.None, UpstreamMark.Ahead, UpstreamMark.None, UpstreamMark.None],
            r.Select(x => x.Upstream));
        Assert.Equal([true, false, false, false, false, false], r.Select(x => x.IsHead));
    }

    [Theory]
    [InlineData(UpstreamState.Gone, "U1")]
    [InlineData(UpstreamState.Valid, "L2")] // tips equal
    public void NoUpstreamMarksWhenGoneOrEqual(UpstreamState state, string upstreamSha) =>
        Assert.All(BuildDiverged(state, upstreamSha), row => Assert.Equal(UpstreamMark.None, row.Upstream));

    static WalkedCommit[] SyntheticHistory(int count)
    {
        // Mainline with a side branch every 10 commits that is merged back 5 commits later.
        var walk = new WalkedCommit[count];
        for (int i = 0; i < count; i++)
        {
            string next = i + 1 < count ? $"c{i + 1}" : "";
            string[] parents = next == "" ? [] : i % 10 == 0 && i + 6 < count ? [next, $"s{i}"] : [next];
            walk[i] = new WalkedCommit($"c{i}", parents);
        }
        // Insert the side commits s{i} (parent c{i+5}) right after c{i+4} so children come first.
        var list = new List<WalkedCommit>(count * 2);
        var side = new Dictionary<int, WalkedCommit>();
        for (int i = 0; i < count; i++)
        {
            list.Add(walk[i]);
            if (side.Remove(i, out var s)) list.Add(s);
            if (walk[i].Parents.Length == 2) side[i + 4] = new WalkedCommit($"s{i}", [$"c{i + 5}"]);
        }
        return [.. list];
    }

    [Fact]
    public void IncrementalAppendEqualsSingleAppend()
    {
        var walk = SyntheticHistory(500);
        var refs = new RefSnapshot([Local("main", "c0", "refs/remotes/origin/main"), Remote("origin/main", "c3"), Local("f", "s20")],
            "c0", "refs/heads/main");
        StashEntry[] stashes = [Stash(0, "st0", "c7"), Stash(1, "st1", "s30")];

        var whole = new CommitListBuilder(refs, stashes).Append(walk).Select(Dump).ToList();
        var paged = new CommitListBuilder(refs, stashes);
        foreach (var page in walk.Chunk(7)) paged.Append(page);

        Assert.Equal(whole, paged.Rows.Select(Dump));
        Assert.Equal(walk.Length + 2, whole.Count);
    }

    [Fact]
    public void HundredThousandCommitsBuildQuickly()
    {
        var walk = SyntheticHistory(100_000);
        var builder = new CommitListBuilder(new RefSnapshot([Local("main", "c0")], "c0", "refs/heads/main"), []);
        var sw = Stopwatch.StartNew();
        foreach (var page in walk.Chunk(10_000)) builder.Append(page);
        sw.Stop();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.All(builder.Rows, r => Assert.True(r.IsReachable));
    }

    [Fact]
    public void WideGraphHitsTheCapAndBuildsQuickly()
    {
        // 250 parallel chains of 100 commits each: 200 get lanes, the rest run into the cap.
        const int chains = 250, depth = 100;
        var walk = new List<WalkedCommit>(chains * depth);
        for (int d = 0; d < depth; d++)
            for (int k = 0; k < chains; k++)
                walk.Add(new WalkedCommit($"k{k}d{d}", d + 1 < depth ? [$"k{k}d{d + 1}"] : []));

        var builder = new CommitListBuilder(new RefSnapshot([], null, null), []);
        var sw = Stopwatch.StartNew();
        foreach (var page in walk.Chunk(10_000)) builder.Append(page);
        sw.Stop();

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
        Assert.Equal(200, builder.Rows.Max(r => r.Graph.Lines.Count));
        Assert.All(builder.Rows, r => Assert.All(r.Graph.Lines, l => Assert.True(l.Top < 200 && l.Bottom < 200)));
        // Chain 249 never gets a lane: plain dot in column 0, no lines from the dot.
        var capped = builder.Rows[chains - 1];
        Assert.Equal(0, capped.Graph.DotColumn);
        Assert.DoesNotContain(capped.Graph.Lines, l => l.Top == -1);
    }
}
