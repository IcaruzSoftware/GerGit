using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Core.Rebase;
using static GerGit.Core.Rebase.RebaseAction;
using static GerGit.Core.Rebase.RebaseGlyph;

namespace GerGit.Tests.Core;

/// <summary>The pure todo model of the interactive rebase and the
/// helper's message lookup.</summary>
public sealed class RebaseTodoTests
{
    static RebaseRow Row(string name, RebaseAction action = Pick, string? message = null, params string[] refs) =>
        new(action, name, "A", "a@x", DateTimeOffset.UnixEpoch, message ?? name, refs);

    static RebaseTodo Todo(params RebaseRow[] rows) => new(rows);

    static string Names(RebaseTodo t) => string.Join(",", t.Rows.Select(r => r.Sha));

    [Fact]
    public void SquashGroup_TargetBecomesReword_WithMessagesTargetFirstThenOlderToNewer()
    {
        var t = Todo(Row("c4", Squash, "four"), Row("c3", Fixup, "three"), Row("c2", Squash, "two"), Row("c1", Pick, "one"), Row("c0"));
        Assert.Equal([Start, Middle, Middle, Target, None], t.Rows.Select(r => r.Glyph));
        Assert.Equal(Reword, t.Rows[3].Action);
        Assert.Equal("one\n\ntwo\n\nfour", t.Rows[3].GroupMessage); // the fixup's message is left out
        Assert.Equal("one", t.Rows[3].Subject);
        Assert.Equal("two\n\nfour", t.Rows[3].Description);
        Assert.Equal(Pick, t.Rows[4].Action);

        // Dissolving the group sends the target back to pick (reword without a custom message).
        t.SetAction([0, 1, 2], Pick);
        Assert.All(t.Rows, r => Assert.Equal((Pick, None, (string?)null), (r.Action, r.Glyph, r.GroupMessage)));
    }

    [Fact]
    public void FixupOnlyGroup_ResetsTargetToPick_UnlessCustomMessageOrEdit()
    {
        var t = Todo(Row("f", Fixup), Row("t"));
        t.SetAction([1], Reword); // reword without a message
        Assert.Equal(Pick, t.Rows[1].Action);

        t.Reword(1, "custom");
        Assert.Equal((Reword, "custom"), (t.Rows[1].Action, t.Rows[1].CustomMessage));

        t.SetAction([1], Edit);
        Assert.Equal((Edit, (string?)null, Target), (t.Rows[1].Action, t.Rows[1].CustomMessage, t.Rows[1].Glyph));
    }

    [Fact]
    public void EditTarget_StaysEdit_AndKeepsItsOwnMessage()
    {
        var t = Todo(Row("s", Squash, "squashed"), Row("t", Edit, "target"));
        Assert.Equal((Edit, (string?)null), (t.Rows[1].Action, t.Rows[1].GroupMessage));
        Assert.Equal(new Dictionary<string, string> { ["s"] = "target", ["t"] = "target" }, RebaseTodo.Archive(t.Rows));
    }

    [Fact]
    public void DropInsideGroup_IsThrough_DropAboveGroup_IsNone()
    {
        var t = Todo(Row("d0", Drop), Row("s", Squash), Row("d1", Drop), Row("f", Fixup), Row("t"));
        Assert.Equal([None, Start, Through, Middle, Target], t.Rows.Select(r => r.Glyph));
        Assert.Equal("t\n\ns", t.Rows[4].GroupMessage);
    }

    [Fact]
    public void OldestRowSquash_HasNoTarget()
    {
        var t = Todo(Row("p"), Row("s", Squash), Row("d", Drop));
        Assert.Equal([None, Start, Through], t.Rows.Select(r => r.Glyph));
        Assert.Equal(Pick, t.Rows[0].Action);
        // No validation: the list is written as is (git fails on it).
        Assert.Equal("d d d\ns s s\np p p\n", RebaseTodo.Text(t.Rows, updateRefs: false));
    }

    [Fact]
    public void CustomMessage_DroppedWhenActionIsNotReword_AndEmptyRewordRevertsToPick()
    {
        var t = Todo(Row("a"), Row("b"));
        t.Reword(0, "new\n\nbody");
        Assert.Equal(("new", "body"), (t.Rows[0].Subject, t.Rows[0].Description));
        t.SetAction([0], Pick);
        t.SetAction([0], Reword);
        Assert.Equal((Pick, (string?)null), (t.Rows[0].Action, t.Rows[0].CustomMessage));

        t.Reword(1, "");
        Assert.Equal(Pick, t.Rows[1].Action);
    }

    [Fact]
    public void MoveUpDown_InsertTheSelectionAsOneBlockNextToTheFirstSelectedIndex()
    {
        var t = Todo(Row("A"), Row("B"), Row("C"), Row("D"), Row("E"));
        Assert.Equal([1, 2], t.MoveUp([4, 2]));
        Assert.Equal("A,C,E,B,D", Names(t));

        t = Todo(Row("A"), Row("B"), Row("C"), Row("D"), Row("E"));
        Assert.Equal([1, 2], t.MoveDown([0, 2]));
        Assert.Equal("B,A,C,D,E", Names(t));

        Assert.Equal([0], t.MoveUp([0]));
        Assert.Equal("B,A,C,D,E", Names(t));
        Assert.Equal([4], t.MoveDown([4]));
        Assert.Equal([3, 4], t.MoveDown([3, 4])); // min(first + 1, remaining 3) = 3
        Assert.Equal("B,A,C,D,E", Names(t));
    }

    [Fact]
    public void MoveTo_InsertsBeforeOrAfterTheTarget_DropOnDraggedRowDoesNothing()
    {
        var t = Todo(Row("A"), Row("B"), Row("C"), Row("D"), Row("E"));
        Assert.Equal([1, 2], t.MoveTo([0, 1], 3, after: false));
        Assert.Equal("C,A,B,D,E", Names(t));

        t = Todo(Row("A"), Row("B"), Row("C"), Row("D"), Row("E"));
        Assert.Equal([2, 3], t.MoveTo([0, 1], 3, after: true));
        Assert.Equal("C,D,A,B,E", Names(t));

        Assert.Equal([2, 3], t.MoveTo([2, 3], 3, after: true));
        Assert.Equal("C,D,A,B,E", Names(t));
        Assert.True(t.Changed);
    }

    [Fact]
    public void MoveRerunsGroupRules()
    {
        var t = Todo(Row("s", Squash), Row("a"), Row("b"));
        Assert.Equal(Reword, t.Rows[1].Action);
        t.MoveDown([0]); // s now melds into b
        Assert.Equal("a,s,b", Names(t));
        Assert.Equal([None, Start, Target], t.Rows.Select(r => r.Glyph));
        Assert.Equal((Pick, Reword), (t.Rows[0].Action, t.Rows[2].Action));
    }

    [Fact]
    public void Changed_ComparesActionAndShaPerIndex()
    {
        var t = Todo(Row("a"), Row("b"));
        Assert.False(t.Changed);
        t.SetAction([0], Drop);
        Assert.True(t.Changed);
        t.SetAction([0], Pick);
        Assert.False(t.Changed);
        t.Reword(1, "x");
        Assert.True(t.Changed);
    }

    [Fact]
    public void QuickAction_AppliesInitialAction_SelectsOldest_AndCountsAsLoaded()
    {
        var t = Todo(Row("c3"), Row("c2"), Row("c1"), Row("p"));
        Assert.Equal(2, t.ApplyQuickAction(QuickAction.Squash, ["c3", "c1"]));
        Assert.Equal([Squash, Reword, Squash, Reword], t.Rows.Select(r => r.Action)); // c2 receives c3
        Assert.False(t.Changed);

        t = Todo(Row("c1"), Row("p"));
        Assert.Equal(0, t.ApplyQuickAction(QuickAction.Reword, ["c1"]));
        Assert.Equal(Pick, t.Rows[0].Action);
        Assert.Equal(0, t.ApplyQuickAction(QuickAction.Edit, ["c1"]));
        Assert.Equal(Edit, t.Rows[0].Action);
        Assert.Null(t.ApplyQuickAction(QuickAction.Drop, ["zzz"]));
    }

    [Fact]
    public void Archive_MapsPendingSquashFixupRowsToTheNextRowsMessage()
    {
        var rows = Todo(Row("c5", Fixup), Row("c4", Drop), Row("c3", Squash), Row("c2"), Row("c1"), Row("c0", Squash)).Rows.ToList();
        var t = new RebaseTodo(rows);
        t.Reword(3, "custom");
        Assert.Equal(new Dictionary<string, string> { ["c5"] = "custom", ["c3"] = "custom", ["c2"] = "custom", ["c1"] = "c1" },
            RebaseTodo.Archive(t.Rows));

        t.Reword(3, null);
        Assert.Equal("c2\n\nc3", RebaseTodo.Archive(t.Rows)["c5"]); // group message
    }

    [Fact]
    public void Text_OldestFirst_WithUpdateRefLinesOnlyWhenAsked()
    {
        var t = Todo(Row("c3", Fixup, "three"), Row("c2", Pick, "two\nbody", "refs/heads/x", "refs/heads/y"), Row("c1", Drop, "one"));
        Assert.Equal("d c1 one\np c2 two\nupdate-ref refs/heads/x\nupdate-ref refs/heads/y\nf c3 three\n", RebaseTodo.Text(t.Rows, true));
        Assert.Equal("d c1 one\np c2 two\nf c3 three\n", RebaseTodo.Text(t.Rows, false));
    }

    const string A = "1111111111111111111111111111111111111111", B = "2222222222222222222222222222222222222222",
        C = "3333333333333333333333333333333333333333";

    [Fact]
    public void ParseTodo_ReadsCommitAndUpdateRefLines_SkipsTheRest()
    {
        var text = $"u refs/heads/before\n# comment\np 1111111 # ~>{A}\n\nu refs/heads/topic\nu refs/heads/gone\n" +
                   $"x make\nb\nl onto\np 2222222 ~>222\np 2222222 no marker {B}\nf 2222222 ~>{B}\r\ns 3333333 ~>{C}\nnoop\n" +
                   "^ Rebase 123..456 onto 123 (2 commands)\n^ p, pick <commit> = use commit\n";
        var entries = RebaseTodo.ParseTodo(text, "~>", new HashSet<string> { "refs/heads/topic", "refs/heads/before" });
        Assert.Equal(3, entries.Count);
        Assert.Equal((Pick, A, "refs/heads/topic"), (entries[0].Action, entries[0].Sha, Assert.Single(entries[0].Refs)));
        Assert.Equal((Fixup, B), (entries[1].Action, entries[1].Sha));
        Assert.Equal((Squash, C), (entries[2].Action, entries[2].Sha));
        Assert.Empty(entries[2].Refs);
    }

    [Fact]
    public void BuildRows_NewestFirst_FromLogFields()
    {
        var entries = RebaseTodo.ParseTodo($"p 1 ~>{A}\nu refs/heads/t\nf 2 ~>{B}\n", "~>", new HashSet<string> { "refs/heads/t" });
        var log = $"{B}\u001fBob\u001fb@x\u001f1700000000\u001fsecond\n\n\nbody\nmore\n\0\n{A}\u001fAnn\u001fa@x\u001f1600000000\u001ffirst\0";
        var rows = RebaseTodo.BuildRows(entries, log, "\x1f", "\0");
        Assert.Equal([B, A], rows.Select(r => r.Sha));
        Assert.Equal((Fixup, "Bob", "b@x", "second", "body\nmore"), (rows[0].Action, rows[0].AuthorName, rows[0].AuthorEmail, rows[0].Subject, rows[0].Description));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), rows[0].AuthorDate);
        Assert.Equal("2222222", rows[0].ShortSha);
        Assert.Equal(["refs/heads/t"], rows[1].Refs);

        // A commit without data must not vanish from the todo written back (git would drop it).
        var withC = RebaseTodo.ParseTodo($"p 1 ~>{A}\np 3 ~>{C}\n", "~>", new HashSet<string>());
        var e = Assert.Throws<GitException>(() => RebaseTodo.BuildRows(withC, log, "\x1f", "\0"));
        Assert.Equal($"No commit data for {C}", e.Message);
    }

    [Fact]
    public void IsParentChain_EachTheSingleParentOfTheOneBefore()
    {
        (string, IReadOnlyList<string>) Commit(string sha, params string[] parents) => (sha, parents);
        Assert.True(RebaseTodo.IsParentChain([Commit("c3", "c2"), Commit("c1", "c0"), Commit("c2", "c1")]));
        Assert.True(RebaseTodo.IsParentChain([Commit("c1")]));
        Assert.False(RebaseTodo.IsParentChain([Commit("c3", "c2"), Commit("c1", "c0")])); // gap
        Assert.False(RebaseTodo.IsParentChain([Commit("m", "c1", "x"), Commit("c1", "c0")])); // merge in the chain
    }

    [Fact]
    public void StepSha_IsTheLastCommitLineOfDone_NoopEndsIt()
    {
        var done = $"pick {A} one\nreword {B} two\nupdate-ref refs/heads/x\n# comment\nexec make\n";
        Assert.Equal(B, RebaseEditor.StepSha("squash 333 three\n", done));
        Assert.Null(RebaseEditor.StepSha("noop\n", done));
        Assert.Null(RebaseEditor.StepSha("", "noop\n"));
        Assert.Null(RebaseEditor.StepSha(null, $"p {A}\n")); // no rest
        Assert.Null(RebaseEditor.StepSha(null, null));
    }

    [Fact]
    public void ApplyArchivedMessage_OverwritesCommitEditMsg_OnlyWhenTheStepIsArchived()
    {
        var gitDir = Directory.CreateTempSubdirectory("gergit-editor-").FullName;
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(gitDir, "rebase-merge")).FullName;
            var msg = Path.Combine(gitDir, "COMMIT_EDITMSG");
            File.WriteAllText(msg, "git's text\n");
            RebaseEditor.ApplyArchivedMessage(msg); // no done, no archive
            Assert.Equal("git's text\n", File.ReadAllText(msg));

            File.WriteAllText(Path.Combine(dir, "done"), $"pick {A} one\nsquash {B} two\n");
            RebaseEditor.ApplyArchivedMessage(msg); // no archive
            Assert.Equal("git's text\n", File.ReadAllText(msg));

            RebaseEditor.WriteArchive(dir, new Dictionary<string, string> { [A] = "one", [B] = "group\n\nmessage" });
            RebaseEditor.ApplyArchivedMessage(msg);
            Assert.Equal("group\n\nmessage\n", File.ReadAllText(msg));

            File.WriteAllText(msg, "git's text\n");
            File.WriteAllText(Path.Combine(dir, "done"), $"pick {C} three\n");
            RebaseEditor.ApplyArchivedMessage(msg);
            Assert.Equal("git's text\n", File.ReadAllText(msg));
        }
        finally { Directory.Delete(gitDir, recursive: true); }
    }

    [Fact]
    public void Helper_LeavesOtherFilesAlone()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "x");
            Assert.Equal(0, RebaseEditor.RunHelper([file]));
            Assert.Equal("x", File.ReadAllText(file));
        }
        finally { File.Delete(file); }
    }
}
