using GerGit.Core;
using GerGit.Core.Graph;

namespace GerGit.Tests.Core;

/// <summary>Lane layout of the commit graph: pass-through lanes, branch tips, merges, roots and the lane cap.</summary>
public class GraphTests
{
    internal static WalkedCommit C(string sha, params string[] parents) => new(sha, parents);

    internal static IReadOnlyList<CommitRow> Build(params WalkedCommit[] walk) =>
        new CommitListBuilder(new RefSnapshot([], null, null), []).Append(walk);

    static GraphLine L(int id, int top, int bottom) => new(id, top, bottom);

    static void AssertRow(CommitRow row, int dot, int dotId, params GraphLine[] lines)
    {
        Assert.Equal(dot, row.Graph.DotColumn);
        Assert.Equal(dotId, row.Graph.DotLaneId);
        Assert.Equal(lines, row.Graph.Lines);
    }

    [Fact]
    public void LinearHistory()
    {
        var r = Build(C("A", "B"), C("B", "C"), C("C"));
        AssertRow(r[0], 0, 0, L(0, -1, 0));
        AssertRow(r[1], 0, 0, L(0, 0, 0));
        AssertRow(r[2], 0, 0, L(0, 0, -1));
        Assert.All(r, row => Assert.False(row.Graph.IsMerge));
    }

    [Fact]
    public void SideBranchMergedBack()
    {
        var r = Build(C("M", "A", "B"), C("B", "R"), C("A", "R"), C("R"));
        AssertRow(r[0], 0, 0, L(0, -1, 0), L(1, -1, 1));
        Assert.True(r[0].Graph.IsMerge);
        AssertRow(r[1], 1, 1, L(0, 0, 0), L(1, 1, 1));
        AssertRow(r[2], 0, 0, L(0, 0, 0), L(1, 1, 1));
        AssertRow(r[3], 0, 0, L(0, 0, -1), L(1, 1, -1));
    }

    [Fact]
    public void LaneShiftsLeftKeepingItsId()
    {
        // Three tips build lanes [0: X, 1: Y, 2: Z]; then Y (no parent) ends lane 1.
        var r = Build(C("T1", "X"), C("T2", "Y"), C("T3", "Z"), C("Y"), C("Z", "Q"));
        AssertRow(r[2], 2, 2, L(0, 0, 0), L(1, 1, 1), L(2, -1, 2));
        AssertRow(r[3], 1, 1, L(0, 0, 0), L(1, 1, -1), L(2, 2, 1));
        // The shifted lane sits in column 1 but keeps id 2.
        AssertRow(r[4], 1, 2, L(0, 0, 0), L(2, 1, 1));
    }

    [Fact]
    public void TwoLanesJoinAtBranchPoint()
    {
        var r = Build(C("A", "P"), C("B", "P"), C("P"));
        AssertRow(r[1], 1, 1, L(0, 0, 0), L(1, -1, 1));
        AssertRow(r[2], 0, 0, L(0, 0, -1), L(1, 1, -1));
    }

    [Fact]
    public void MergeLineToExistingLaneTakesThatLanesId()
    {
        // Lane 0 already waits for B when the merge M (parents A, B) arrives as a tip.
        var r = Build(C("T", "B"), C("M", "A", "B"));
        AssertRow(r[1], 1, 1, L(0, 0, 0), L(1, -1, 1), L(0, -1, 0));
    }

    [Fact]
    public void OctopusMergeHandlesEveryOtherParent()
    {
        // Lane 0 waits for C; tip M (parents A, B, C): A gets new lane 1, B new lane 2, C joins lane 0.
        var r = Build(C("T", "C"), C("M", "A", "B", "C"));
        AssertRow(r[1], 1, 1, L(0, 0, 0), L(1, -1, 1), L(2, -1, 2), L(0, -1, 0));
        Assert.True(r[1].Graph.IsMerge);
    }

    [Fact]
    public void RootTipWithoutLanesHasPlainDotInColumnZero()
    {
        var r = Build(C("R"));
        AssertRow(r[0], 0, 0);
    }

    [Fact]
    public void LanesAreCappedAt200()
    {
        var walk = Enumerable.Range(0, 200).Select(i => C($"T{i}", $"P{i}")).ToList();
        walk.Add(C("Extra", "PX"));
        walk.Add(C("Merge", "PY", "P0"));
        var r = Build([.. walk]);

        Assert.Equal(200, r[199].Graph.Lines.Count);
        foreach (var row in r.Skip(200))
        {
            // No new lane, no merge line, plain dot in column 0; the 200 lanes pass straight through.
            Assert.Equal(0, row.Graph.DotColumn);
            Assert.Equal(200, row.Graph.Lines.Count);
            Assert.All(row.Graph.Lines, l => Assert.Equal(l.Top, l.Bottom));
        }
    }
}
