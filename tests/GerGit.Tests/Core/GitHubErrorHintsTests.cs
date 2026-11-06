using GerGit.Core;

namespace GerGit.Tests.Core;

/// <summary>GitHub "Repository not found" hints.
/// In the Askpass collection: it sets the process-wide GitErrors hooks that HostingService.Install sets too.</summary>
[Collection("Askpass")]
public class GitHubErrorHintsTests
{
    const string NotFound = "remote: Repository not found.\nfatal: repository 'https://github.com/o/r.git/' not found\n";

    static GitError Classify(string stderr, int accounts, string? url = null)
    {
        var (count, link) = (GitErrors.GitHubAccountCount, GitErrors.GitHubAccessUrl);
        try
        {
            GitErrors.GitHubAccountCount = () => accounts;
            if (url is not null) GitErrors.GitHubAccessUrl = url;
            return GitErrors.Classify(new GitResult(128, "", stderr));
        }
        finally { (GitErrors.GitHubAccountCount, GitErrors.GitHubAccessUrl) = (count, link); }
    }

    [Fact]
    public void NoAccount_NoHints()
    {
        var e = Classify(NotFound, 0);
        Assert.Equal(GitErrorKind.AuthenticationFailed, e.Kind);
        Assert.Equal(NotFound, e.Text);
    }

    [Fact]
    public void OneAccount_OrganizationHint_WithTheTokensPageByDefault() =>
        Assert.Equal(NotFound +
            "gergit: * ensure GerGit has access to your organization:\n" +
            "gergit:   https://github.com/settings/tokens", Classify(NotFound, 1).Text);

    [Fact]
    public void TwoAccounts_BothHints_WithTheConfiguredUrl() =>
        Assert.Equal(NotFound +
            "gergit: * ensure GerGit has access to your organization:\n" +
            "gergit:   https://github.com/settings/connections/applications/abc\n" +
            "gergit: * ensure GerGit uses correct account for the remote\n" +
            "gergit:   (right click on the remote on sidebar and select Edit)",
            Classify(NotFound, 2, "https://github.com/settings/connections/applications/abc").Text);

    [Fact]
    public void OtherHost_IsGeneric() =>
        Assert.Equal(GitErrorKind.Generic,
            Classify("remote: Repository not found.\nfatal: repository 'https://gitlab.com/o/r.git/' not found\n", 2).Kind);
}
