using System.Diagnostics;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>The credential-helper override as environment entries.</summary>
public class CredentialOverrideTests
{
    [Fact]
    public void EmptyEnvironment_ClearsThenAddsTheQuotedHelper()
    {
        var env = new Dictionary<string, string?>();
        GitRunner.AddCredentialOverride(env, "/run/user/1 x/it's.askpass", manager: false);
        Assert.Equal(new Dictionary<string, string?>
        {
            ["GIT_CONFIG_KEY_0"] = "credential.helper", ["GIT_CONFIG_VALUE_0"] = "",
            ["GIT_CONFIG_KEY_1"] = "credential.helper", ["GIT_CONFIG_VALUE_1"] = "!'/run/user/1 x/it'\\''s.askpass'",
            ["GIT_CONFIG_COUNT"] = "2",
        }, env);
    }

    [Fact]
    public void ParentEntries_AreKept_AndManagerComesLast()
    {
        var env = new Dictionary<string, string?>
        {
            ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "user.name", ["GIT_CONFIG_VALUE_0"] = "Parent",
        };
        GitRunner.AddCredentialOverride(env, "/h", manager: true);
        Assert.Equal("4", env["GIT_CONFIG_COUNT"]);
        Assert.Equal(("user.name", "Parent"), (env["GIT_CONFIG_KEY_0"], env["GIT_CONFIG_VALUE_0"]));
        Assert.Equal(new string?[] { "", "!'/h'", "manager" }, new[] { env["GIT_CONFIG_VALUE_1"], env["GIT_CONFIG_VALUE_2"], env["GIT_CONFIG_VALUE_3"] });
        Assert.All([env["GIT_CONFIG_KEY_1"], env["GIT_CONFIG_KEY_2"], env["GIT_CONFIG_KEY_3"]], k => Assert.Equal("credential.helper", k));
    }
}

/// <summary>Credential-helper mode of the askpass helper and the account lookup (including HTTPS git operations with account credentials), end to end over HTTP with real git.</summary>
[Collection("Askpass")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class CredentialHelperTests : IDisposable
{
    const string User = "alice", Token = "tok-3f9a1c77e2";

    readonly Func<CredentialPrompt, Task<string?>> _prompt = Askpass.PromptCredentials;
    readonly Func<CredentialQuery, Task<CredentialAnswer?>> _lookup = Askpass.LookupCredential;
    readonly List<CredentialQuery> _queries = [];
    int _prompts;

    public CredentialHelperTests()
    {
        Askpass.Start();
        Assert.NotNull(Askpass.HelperPath);
        Askpass.PromptCredentials = _ => { Interlocked.Increment(ref _prompts); return Task.FromResult<string?>(null); };
    }

    public void Dispose()
    {
        GitRunner.CredentialHelperEnabled = false;
        (Askpass.PromptCredentials, Askpass.LookupCredential) = (_prompt, _lookup);
        Askpass.Stop();
    }

    /// <summary>Answers <paramref name="token"/> for the given host, records every query.</summary>
    void Accounts(string host, string token) => Askpass.LookupCredential = q =>
    {
        lock (_queries) _queries.Add(q);
        return Task.FromResult(q.Protocol == "http" && q.Host == host ? new CredentialAnswer(User, token) : null);
    };

    [Fact]
    public async Task RealGit_SeesTheChainOnlyWhenEnabled_AndTheHeaderStaysPlain()
    {
        using var repo = new TempRepo();
        string[] args = ["config", "--show-scope", "--get-all", "credential.helper"];

        var job = new Job("x");
        Assert.Empty(CommandScope((await GitRunner.RunJobAsync(job, repo.Path, args)).Stdout));

        GitRunner.CredentialHelperEnabled = true;
        job = new Job("x");
        var helpers = CommandScope((await GitRunner.RunJobAsync(job, repo.Path, args)).Stdout);
        Assert.Equal(["", "!" + Askpass.ShQuote(Askpass.HelperPath!)], helpers[..2]);
        Assert.StartsWith("$ git config --show-scope --get-all credential.helper\n\n", job.Output);

        static string[] CommandScope(string stdout) =>
            stdout.Split('\n').Where(l => l.StartsWith("command\t", StringComparison.Ordinal)).Select(l => l["command\t".Length..]).ToArray();
    }

    [Fact]
    public async Task Get_ParsesGitsLines_AndAnswersInGitsFormat()
    {
        Accounts("127.0.0.1:8080", Token);
        var (answer, after) = await Askpass.AnswerAsync("credential", true, "/r",
            "protocol=http\nhost=127.0.0.1:8080\nusername=someone\npath=x.git\n");
        Assert.Null(after);
        Assert.Equal($"protocol=http\nhost=127.0.0.1:8080\nusername={User}\npassword={Token}\n\n", answer);
        Assert.Equal([new CredentialQuery("http", "127.0.0.1:8080", "someone", "/r")], _queries);
        Assert.Equal(0, _prompts); // never a dialog, also when silent
    }

    [Theory]
    [InlineData("protocol=http\nhost=other.example\n")] // no account
    [InlineData("host=127.0.0.1:8080\n")]                // no protocol
    [InlineData("protocol=http\n")]                      // no host
    public async Task Get_WithoutMatch_AnswersEmpty(string request)
    {
        Accounts("127.0.0.1:8080", Token);
        Assert.Equal("", (await Askpass.AnswerAsync("credential", false, "", request)).Answer);
        Assert.Equal(0, _prompts);
    }

    [Fact]
    public async Task Get_TokenWithLineBreak_AnswersEmpty()
    {
        Accounts("h", "tok\npassword=other");
        Assert.Equal("", (await Askpass.AnswerAsync("credential", false, "", "protocol=http\nhost=h\n")).Answer);
    }

    [Fact]
    public void CredentialAnswer_ToString_HidesTheToken() =>
        Assert.DoesNotContain(Token, new CredentialAnswer(User, Token).ToString());

    [Fact]
    public async Task HelperProcess_Get_PrintsTheAnswer_EmptyExitsNonZero_StoreAndEraseDoNothing()
    {
        Accounts("h:1", Token);
        var request = "protocol=http\nhost=h:1\n";
        var (code, stdout) = await RunHelper("get", request, silent: true);
        Assert.Equal(0, code);
        Assert.Equal($"protocol=http\nhost=h:1\nusername={User}\npassword={Token}\n\n", stdout);

        (code, stdout) = await RunHelper("get", "protocol=http\nhost=nope\n", silent: false);
        Assert.NotEqual(0, code);
        Assert.Equal("", stdout);

        int asked = _queries.Count;
        Assert.Equal((0, ""), await RunHelper("store", request + "username=alice\npassword=x\n", silent: false));
        Assert.Equal((0, ""), await RunHelper("erase", request, silent: false));
        Assert.Equal(asked, _queries.Count); // not forwarded
        Assert.Equal(0, _prompts);
    }

    /// <summary>The helper script exactly as git runs a credential helper: `&lt;script&gt; get` with the request on stdin.</summary>
    static async Task<(int Code, string Stdout)> RunHelper(string op, string stdin, bool silent)
    {
        var psi = new ProcessStartInfo(Askpass.HelperPath!, op) { RedirectStandardInput = true, RedirectStandardOutput = true };
        psi.Environment["GERGIT_PID"] = Environment.ProcessId.ToString();
        psi.Environment["GERGIT_NO_PROMPT"] = silent ? "1" : "0";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        try { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
        catch (IOException) { } // store/erase exit without reading
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout);
    }

    [Fact]
    public async Task Http_CloneFetchPush_UseTheAccountToken_WithoutAPrompt()
    {
        using var server = new GitHttpServer(User, Token);
        using var seed = new TempRepo();
        seed.Commit("first", 1, ("a.txt", "a\n"));
        var bare = server.AddRepo("repo", seed.Path);
        Accounts($"127.0.0.1:{server.Port}", Token);
        GitRunner.CredentialHelperEnabled = true;

        // Clone (the git part only).
        using var clone = new TempRepo(init: false);
        var job = new Job("Clone");
        var r = await GitRunner.RunJobAsync(job, clone.Path, ["clone", server.Url + "repo.git", ".", "--progress"]);
        Assert.True(r.Success, r.ReadableOutput);
        clone.Git("config", "user.name", "Test User");
        clone.Git("config", "user.email", "test@example.com");
        var repo = await GitRepository.OpenAsync(clone.Path);

        // Fetch a commit someone pushed meanwhile.
        seed.Commit("second", 2, ("a.txt", "b\n"));
        seed.Git("push", "-q", bare, "main");
        job = new Job("Fetch 'origin'");
        r = await repo.FetchAsync(job, "origin", tags: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal("Fetched 1 branch", job.Message);

        // Push a new commit.
        clone.Git("reset", "-q", "--hard", "origin/main");
        var sha = clone.Commit("third", 3, ("c.txt", "c\n"));
        job = new Job("Push");
        r = await repo.PushAsync(job, "origin", "refs/heads/main", force: false, tags: false, setUpstream: false);
        Assert.True(r.Success, r.ReadableOutput);
        Assert.Equal(sha, seed.Git("ls-remote", bare, "refs/heads/main").Split('\t')[0]);

        Assert.Equal(0, _prompts);
        Assert.NotEmpty(_queries);
        Assert.All(_queries, q => Assert.Equal($"127.0.0.1:{server.Port}", q.Host));
    }

    [Fact]
    public async Task Http_WrongToken_FailsWithAuthError_AndTheTokenStaysHidden()
    {
        const string wrong = "wrong-9d2e4b";
        using var server = new GitHttpServer(User, Token);
        using var seed = new TempRepo();
        seed.Commit("first", 1, ("a.txt", "a\n"));
        server.AddRepo("repo", seed.Path);
        Accounts($"127.0.0.1:{server.Port}", wrong);
        GitRunner.CredentialHelperEnabled = true;

        using var clone = new TempRepo(init: false);
        var job = new Job("Clone");
        var r = await GitRunner.RunJobAsync(job, clone.Path, ["clone", server.Url + "repo.git", ".", "--progress"]);
        Assert.False(r.Success);
        Assert.False(r.Cancelled);
        Assert.Equal(GitErrorKind.AuthenticationFailed, GitErrors.Classify(r).Kind);
        Assert.True(server.Rejected > 0);
        Assert.Equal(0, _prompts);
        foreach (var text in new[] { r.Stdout, r.Stderr, r.ReadableOutput, job.Output, GitErrors.Classify(r).Text })
            Assert.DoesNotContain(wrong, text);
    }
}
