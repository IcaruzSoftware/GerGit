using GerGit.Core;

namespace GerGit.Tests.Core;

public class GitErrorsTests
{
    static GitError Err(string stderr, string stdout = "") => GitErrors.Classify(new GitResult(1, stdout, stderr));

    [Fact]
    public void Authentication_failed_appends_the_credential_hint()
    {
        var e = Err("remote: Invalid username or password.\nfatal: Authentication failed for 'https://h/x.git/'\n");
        Assert.Equal(GitErrorKind.AuthenticationFailed, e.Kind);
        Assert.EndsWith("\n" + GitErrors.CredentialHint, e.Text);
        Assert.Contains("Accounts window (File > Accounts...)", GitErrors.CredentialHint); // Linux has no Credential Manager button
        Assert.StartsWith("remote: Invalid", e.Text);
    }

    [Fact]
    public void Tag_clobber() =>
        Assert.Equal(GitErrorKind.TagClobber, Err(" ! [rejected]        v1 -> v1  (would clobber existing tag)\n").Kind);

    [Fact]
    public void Index_lock()
    {
        Assert.Equal(GitErrorKind.IndexLocked,
            Err("fatal: Unable to create '/r/.git/index.lock': File exists.\n\nAnother git process seems to be running").Kind);
        Assert.Equal(GitErrorKind.Generic, Err("error: x\nfatal: Unable to create '/r/.git/index.lock': File exists.").Kind); // must start with it
    }

    [Fact]
    public void Patch_does_not_apply_appends_the_whitespace_hint()
    {
        var e = Err("error: patch failed: a.txt:1\nerror: a.txt: patch does not apply\n");
        Assert.Equal(GitErrorKind.PatchDoesNotApply, e.Kind);
        Assert.EndsWith("patch does not apply\ngergit: try to disable 'ignore whitespaces'", e.Text);
    }

    [Fact]
    public void Merge_conflict_is_read_from_stdout()
    {
        Assert.Equal(GitErrorKind.MergeConflict,
            Err("", "Auto-merging a.txt\nCONFLICT (content): Merge conflict in a.txt\nAutomatic merge failed; fix conflicts\n").Kind);
        Assert.Equal(GitErrorKind.MergeConflict, Err("", "CONFLICT (add/add): Merge conflict in a.txt\n").Kind);
        Assert.Equal(GitErrorKind.MergeConflict, Err("", "CONFLICT (modify/delete): a.txt deleted in HEAD and modified in x.\n").Kind);
        Assert.Equal(GitErrorKind.Generic, Err("CONFLICT (content): Merge conflict in a.txt").Kind); // stderr does not count
        Assert.Equal(GitErrorKind.MergeConflict, GitErrors.Classify("CONFLICT (content): Merge conflict in a.txt").Kind); // text form
    }

    [Fact]
    public void Unrelated_histories() =>
        Assert.Equal(GitErrorKind.UnrelatedHistories, Err("fatal: refusing to merge unrelated histories\n").Kind);

    [Fact]
    public void Unsafe_repository_paths_are_extracted()
    {
        var dubious = Err("fatal: detected dubious ownership in repository at '/srv/my repo'\nTo add an exception ...");
        Assert.Equal((GitErrorKind.UnsafeRepository, "/srv/my repo"), (dubious.Kind, dubious.Path));
        var old = Err("fatal: unsafe repository ('/srv/r' is owned by someone else)\n");
        Assert.Equal((GitErrorKind.UnsafeRepository, "/srv/r"), (old.Kind, old.Path));
    }

    [Fact]
    public void Could_not_resolve_host_gives_the_short_job_message()
    {
        var e = Err("fatal: unable to access 'https://nohost.invalid/x.git/': Could not resolve host: nohost.invalid  \n");
        Assert.Equal(GitErrorKind.CouldNotResolveHost, e.Kind);
        Assert.Equal("Could not resolve host 'nohost.invalid'", e.ShortMessage);
    }

    [Fact]
    public void Anything_else_is_generic_with_the_readable_output()
    {
        var e = GitErrors.Classify(new GitResult(1, "out", "fatal: bad thing"));
        Assert.Equal((GitErrorKind.Generic, "out\nfatal: bad thing", "out\nfatal: bad thing"), (e.Kind, e.Text, e.ShortMessage));
    }
}
