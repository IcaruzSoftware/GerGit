using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>The askpass server and its delegates are process-wide: these tests run alone.</summary>
[CollectionDefinition("Askpass", DisableParallelization = true)]
public sealed class AskpassCollection;

/// <summary>Askpass helper, IPC and prompts.</summary>
[Collection("Askpass")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class AskpassTests : IDisposable
{
    const string HostKeyText =
        "The authenticity of host '[127.0.0.1]:2222 ([127.0.0.1]:2222)' can't be established.\n" +
        "ED25519 key fingerprint is SHA256:47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU.\n" +
        "This key is not known by any other names.\n" +
        "Are you sure you want to continue connecting (yes/no/[fingerprint])? ";

    readonly string _dir = Directory.CreateTempSubdirectory("gergit-askpass-").FullName;
    readonly string _knownHosts = Askpass.KnownHostsFile;
    readonly Func<CredentialPrompt, Task<string?>> _credentials = Askpass.PromptCredentials;
    readonly Func<HostKeyPrompt, Task<HostKeyAnswer>> _hostKey = Askpass.PromptHostKey;

    public AskpassTests()
    {
        Askpass.Start();
        Assert.NotNull(Askpass.HelperPath);
    }

    public void Dispose()
    {
        Askpass.Stop();
        (Askpass.KnownHostsFile, Askpass.PromptCredentials, Askpass.PromptHostKey) = (_knownHosts, _credentials, _hostKey);
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Answers every request with 401 + Basic challenge and records the Authorization headers.</summary>
    sealed class AuthServer : IDisposable
    {
        readonly HttpListener _listener = new();
        public int Port { get; }
        public List<string?> Authorizations { get; } = [];

        public AuthServer()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
            _listener.Start();
            _ = LoopAsync();
        }

        async Task LoopAsync()
        {
            while (true)
            {
                HttpListenerContext c;
                try { c = await _listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
                lock (Authorizations) Authorizations.Add(c.Request.Headers["Authorization"]);
                c.Response.StatusCode = 401;
                c.Response.AddHeader("WWW-Authenticate", "Basic realm=\"test\"");
                c.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    /// <summary>git prefers GIT_ASKPASS / core.askPass over SSH_ASKPASS (e.g. VS Code terminals set it): our helper
    /// is never asked then.</summary>
    static async Task SkipIfUserAskpass()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("GIT_ASKPASS") is { Length: > 0 },
            "GIT_ASKPASS is set in this environment; git would not use GerGit's SSH_ASKPASS helper");
        var r = await GitRunner.RunAsync(null, ["config", "--global", "core.askPass"]);
        Assert.SkipWhen(r.Stdout.Trim().Length > 0, "core.askPass is set globally; git would not use GerGit's SSH_ASKPASS helper");
    }

    static string[] LsRemote(AuthServer s) => ["-c", "credential.helper=", "ls-remote", $"http://127.0.0.1:{s.Port}/x.git"];

    static async Task<(int Code, string Out, string Err)> RunProcess(string exe, IEnumerable<string> args, Dictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        return (p.ExitCode, await stdout, await stderr);
    }

    [Fact]
    public async Task HttpsCredentials_RoundTripThroughGitAndTheHelper()
    {
        await SkipIfUserAskpass();
        using var server = new AuthServer();
        using var repo = new TempRepo();
        var prompts = new List<CredentialPrompt>();
        Askpass.PromptCredentials = p =>
        {
            lock (prompts) prompts.Add(p);
            return Task.FromResult<string?>(p.Kind == PromptKind.UserName ? "alice" : "s3cret");
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var r = await GitRunner.RunAsync(repo.Path, LsRemote(server), ct: cts.Token);

        Assert.False(r.Cancelled);
        Assert.False(r.Success); // the server never accepts
        Assert.Equal(
        [
            new CredentialPrompt(repo.Path, PromptKind.UserName, $"Username for 'http://127.0.0.1:{server.Port}': ", null),
            new CredentialPrompt(repo.Path, PromptKind.Password, $"Password for 'http://alice@127.0.0.1:{server.Port}': ", null),
        ], prompts);
        Assert.Contains("Basic " + Convert.ToBase64String("alice:s3cret"u8), server.Authorizations);
    }

    [Fact]
    public async Task CancelledPrompt_GitFailsWithItsOwnMessage()
    {
        await SkipIfUserAskpass();
        using var server = new AuthServer();
        using var repo = new TempRepo();
        int calls = 0;
        Askpass.PromptCredentials = _ => { Interlocked.Increment(ref calls); return Task.FromResult<string?>(null); };
        var r = await GitRunner.RunAsync(repo.Path, LsRemote(server));
        Assert.False(r.Success);
        Assert.False(r.Cancelled);
        Assert.Equal(1, calls);
        Assert.Contains($"could not read Username for 'http://127.0.0.1:{server.Port}'", r.ReadableOutput);
        Assert.All(server.Authorizations, Assert.Null);
    }

    [Fact]
    public async Task Silent_NeverCallsTheDelegate()
    {
        await SkipIfUserAskpass();
        using var server = new AuthServer();
        using var repo = new TempRepo();
        int calls = 0;
        Askpass.PromptCredentials = _ => { Interlocked.Increment(ref calls); return Task.FromResult<string?>("x"); };
        var r = await GitRunner.RunJobAsync(new Job("Automatic fetch"), repo.Path, LsRemote(server), silent: true);
        Assert.False(r.Success);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Environment_PointsSshAtTheHelper()
    {
        using var repo = new TempRepo();
        var r = await GitRunner.RunAsync(repo.Path, ["-c", "alias.envtest=!echo \"$SSH_ASKPASS|$SSH_ASKPASS_REQUIRE\"", "envtest"]);
        Assert.Equal($"{Askpass.HelperPath}|force", r.Stdout.Trim());

        // Silent requests also keep Git Credential Manager from asking.
        var silent = await GitRunner.RunJobAsync(new Job("x"), repo.Path,
            ["-c", "alias.envtest=!echo \"$GERGIT_NO_PROMPT|$GCM_INTERACTIVE\"", "envtest"], silent: true);
        Assert.Equal("1|never", silent.Stdout.Trim());
    }

    [Fact]
    public async Task Silent_PassphrasePrompt_AnswersEmptyWithoutADialog()
    {
        int calls = 0;
        Askpass.PromptCredentials = _ => { calls++; return Task.FromResult<string?>("secret"); };
        var (answer, after) = await Askpass.AnswerAsync("askpass", true, "", "Enter passphrase for key '/home/me/.ssh/id_ed25519': ");
        Assert.Equal("", answer);
        Assert.Null(after);
        Assert.Equal(0, calls);
        Assert.Equal("secret", (await Askpass.AnswerAsync("askpass", false, "", "Enter passphrase for key '/home/me/.ssh/id_ed25519': ")).Answer);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Stop_RemovesSocketAndScript()
    {
        var script = Askpass.HelperPath!;
        var socket = Askpass.SocketPath(Environment.ProcessId);
        Assert.True(File.Exists(script) && File.Exists(socket));
        Askpass.Stop();
        Assert.Null(Askpass.HelperPath);
        Assert.False(File.Exists(script) || File.Exists(socket));
    }

    [Fact]
    public async Task HostKey_Once_AnswersYesAndRemovesTheAppendedLines()
    {
        var known = Path.Combine(_dir, "known_hosts");
        const string original = "github.com ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAIOMqqnkVzrm0SdG6UOoqKLsabgH5C9okWi0dh2l9GKJl\n# no line break at the end";
        File.WriteAllText(known, original);
        Askpass.KnownHostsFile = known;
        HostKeyPrompt? seen = null;
        Askpass.PromptHostKey = p => { seen = p; return Task.FromResult(HostKeyAnswer.Once); };

        // The helper exactly as ssh starts it: the SSH_ASKPASS script with the question as its argument.
        var (code, stdout, stderr) = await RunProcess(Askpass.HelperPath!, [HostKeyText],
            new() { ["GERGIT_PID"] = Environment.ProcessId.ToString(), ["GERGIT_REPO_PATH"] = "/some/repo", ["GERGIT_NO_PROMPT"] = "0" });
        Assert.True(code == 0, stderr);
        Assert.Equal("yes", stdout);
        Assert.Equal(new HostKeyPrompt("/some/repo", HostKeyText), seen);

        // What ssh does after "yes" (it ends an unterminated file first).
        File.AppendAllText(known, "\n[127.0.0.1]:2222 ssh-ed25519 AAAAC3NzaC1lZDI1NTE5AAAAINEW\n");
        var sw = Stopwatch.StartNew();
        while (File.ReadAllText(known) != original && sw.Elapsed < TimeSpan.FromSeconds(20)) await Task.Delay(50);
        Assert.Equal(Encoding.UTF8.GetBytes(original), File.ReadAllBytes(known));
    }

    [Fact]
    public async Task HostKey_AnswerMapping()
    {
        int calls = 0;
        var answer = HostKeyAnswer.Cancel;
        Askpass.PromptHostKey = _ => { calls++; return Task.FromResult(answer); };

        Assert.Equal("no", await Answer(false));
        answer = HostKeyAnswer.Always;
        Assert.Equal("yes", await Answer(false));
        Assert.Equal(2, calls);
        // Silent: refused without a dialog, never answered by the helper itself.
        Assert.Equal("no", await Answer(true));
        Assert.Equal(2, calls);

        static async Task<string> Answer(bool silent)
        {
            var (text, after) = await Askpass.AnswerAsync("askpass", silent, "", HostKeyText);
            Assert.Null(after); // only "once" cleans up known_hosts afterwards
            return text;
        }
    }

    [Fact]
    public void HostKey_Recognised()
    {
        Assert.True(Askpass.IsHostKeyQuestion(HostKeyText));
        Assert.True(Askpass.IsHostKeyQuestion(
            "Warning: the ECDSA host key for 'h' differs from the key for the IP address '1.2.3.4'\nOffending key for IP in /home/u/.ssh/known_hosts:3\nAre you sure you want to continue connecting (yes/no)? "));
        Assert.False(Askpass.IsHostKeyQuestion("Enter passphrase for key '/home/u/.ssh/id_ed25519': "));
    }

    [Theory]
    [InlineData("Username for 'https://github.com': ", PromptKind.UserName, null)]
    [InlineData("Enter passphrase for key '/home/me/.ssh/id_ed25519': ", PromptKind.SshPassphrase, "/home/me/.ssh/id_ed25519")]
    [InlineData("me@example.com's password: ", PromptKind.SshPassword, "me@example.com")]
    [InlineData("example.com's password: ", PromptKind.Password, null)]
    [InlineData("Enter passphrase (empty for no passphrase): ", PromptKind.OtherPassphrase, null)]
    [InlineData("Password for 'https://user@github.com': ", PromptKind.Password, null)]
    public void ParsePrompt(string text, PromptKind kind, string? target) =>
        Assert.Equal(new CredentialPrompt("/r", kind, text, target), Askpass.ParsePrompt("/r", text));

    [Fact]
    public async Task Helper_WithoutRunningApp_FailsWithAMessage()
    {
        var appHost = Path.Combine(AppContext.BaseDirectory, "GerGit");
        var (code, stdout, stderr) = await RunProcess(appHost, ["--askpass", "Password: "],
            new() { ["GERGIT_PID"] = int.MaxValue.ToString() });
        Assert.NotEqual(0, code);
        Assert.Equal("", stdout);
        Assert.Contains("GerGit process not found", stderr);

        Assert.Equal(0, Askpass.RunHelper(["store"])); // credential-helper mode: `get` is in CredentialHelperTests
    }
}
