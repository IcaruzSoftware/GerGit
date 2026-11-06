using System.Text.RegularExpressions;

namespace GerGit.Core;

/// <summary>The special cases of that the error window or the job message treat differently.</summary>
public enum GitErrorKind
{
    Generic,
    AuthenticationFailed,
    TagClobber,
    IndexLocked,
    PatchDoesNotApply,
    MergeConflict,
    UnrelatedHistories,
    UnsafeRepository,
    CouldNotResolveHost,
}

/// <summary>A classified failure. Text = what the window shows (with appended hint lines). Path = the folder to mark as
/// safe (UnsafeRepository, null = use the repository path). Host = the unresolved host (CouldNotResolveHost).</summary>
public sealed record GitError(GitErrorKind Kind, string Text, string? Path = null, string? Host = null)
{
    /// <summary>The short reason for the job message.</summary>
    public string ShortMessage => Kind == GitErrorKind.CouldNotResolveHost ? $"Could not resolve host '{Host}'" : Text.Trim();
}

/// <summary>Classifies failed git results; the rules are checked in order and the first match wins.</summary>
public static partial class GitErrors
{
    /// <summary>The app's own hint lines start with this prefix (coloured as hints, Activity Manager).</summary>
    public const string HintPrefix = "gergit:";

    // Linux has no credential manager window to open:
    // instead, this line points at the Accounts window and the keyring.
    public const string CredentialHint = HintPrefix + " stored credentials can be changed in the Accounts window (File > Accounts...) or in the desktop keyring";

    /// <summary>Number of GitHub (github.com) accounts, for the "Repository not found" hints; set by the app.</summary>
    public static Func<int> GitHubAccountCount { get; set; } = () => 0;

    /// <summary>The page where the user grants GerGit access to an organization. The app sets
    /// `https://github.com/settings/connections/applications/&lt;client id&gt;` when an OAuth client id is configured.</summary>
    public static string GitHubAccessUrl { get; set; } = "https://github.com/settings/tokens";

    public static GitError Classify(GitResult result) => Classify(result.Stdout, result.Stderr, result.ReadableOutput);

    /// <summary>For an error that only has its text (a <see cref="GitException"/> message): every rule looks at the whole text.</summary>
    public static GitError Classify(string text) => Classify(text, text, text);

    static GitError Classify(string stdout, string stderr, string text)
    {
        // Not built yet: LFS "Force Unlock" and the commit-hook case need LFS and the commit job; the cherry-pick /
        // local-changes cases are handled by their operations.
        if (stderr.Contains("fatal: Authentication failed"))
            return new(GitErrorKind.AuthenticationFailed, AppendLine(text, CredentialHint));
        if (stderr.Contains("remote: Repository not found.") && stderr.Contains("github.com"))
        {
            int accounts = GitHubAccountCount();
            if (accounts >= 1)
                text = AppendLine(AppendLine(text, HintPrefix + " * ensure GerGit has access to your organization:"), HintPrefix + "   " + GitHubAccessUrl);
            if (accounts >= 2)
                text = AppendLine(AppendLine(text, HintPrefix + " * ensure GerGit uses correct account for the remote"),
                    HintPrefix + "   (right click on the remote on sidebar and select Edit)");
            return new(GitErrorKind.AuthenticationFailed, text);
        }
        if (stderr.Contains("(would clobber existing tag)"))
            return new(GitErrorKind.TagClobber, text);
        if (stderr.TrimStart().StartsWith("fatal: Unable to create", StringComparison.Ordinal) && stderr.Contains("index.lock': File exists."))
            return new(GitErrorKind.IndexLocked, text);
        if (stderr.TrimStart().StartsWith("error: patch failed", StringComparison.Ordinal) && stderr.Contains("patch does not apply"))
            return new(GitErrorKind.PatchDoesNotApply, AppendLine(text, HintPrefix + " try to disable 'ignore whitespaces'"));
        // names only "CONFLICT (content)"; add/add, modify/delete and rename conflicts are the same state
        // (a plain "unexpected error" for them is a bug).
        if (stdout.Contains("CONFLICT ("))
            return new(GitErrorKind.MergeConflict, text);
        if (stderr.Contains("fatal: refusing to merge unrelated histories"))
            return new(GitErrorKind.UnrelatedHistories, text);
        if ((stderr.Contains("fatal: unsafe repository ") && stderr.Contains("is owned by someone else)"))
            || stderr.Contains("detected dubious ownership in repository at"))
        {
            var m = UnsafeRegex().Match(stderr);
            if (!m.Success) m = DubiousRegex().Match(stderr);
            return new(GitErrorKind.UnsafeRepository, text, m.Success ? m.Groups[1].Value : null);
        }
        if (ResolveHostRegex().Match(stderr) is { Success: true } host)
            return new(GitErrorKind.CouldNotResolveHost, text, Host: host.Groups[1].Value.TrimEnd());
        return new(GitErrorKind.Generic, text);
    }

    static string AppendLine(string text, string line) => text.EndsWith('\n') ? text + line : text + "\n" + line;

    [GeneratedRegex(@"fatal: unsafe repository \('(.+)'")]
    private static partial Regex UnsafeRegex();

    [GeneratedRegex(@"fatal: detected dubious ownership in repository at '(.+)'")]
    private static partial Regex DubiousRegex();

    [GeneratedRegex(@" Could not resolve host: (.+)")]
    private static partial Regex ResolveHostRegex();
}
