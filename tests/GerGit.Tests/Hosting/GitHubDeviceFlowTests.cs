using GerGit.Hosting;

namespace GerGit.Tests.Hosting;

/// <summary>GitHub OAuth device flow against the fake server; the poll wait is recorded, not slept.</summary>
public sealed class GitHubDeviceFlowTests : IDisposable
{
    readonly FakeHostingServer _server = new();
    readonly List<TimeSpan> _waits = [];

    public void Dispose() => _server.Dispose();

    GitHubDeviceFlow Flow(Func<TimeSpan, CancellationToken, Task>? delay = null) => new("client-123")
    {
        WebBase = _server.BaseUri,
        Delay = delay ?? ((t, _) => { _waits.Add(t); return Task.CompletedTask; }),
    };

    static GitHubDeviceFlow.DeviceCode Code => new("dev-code", "ABCD-1234", "https://github.com/login/device", 900, 5);

    void Answers(params object[] answers)
    {
        int i = 0;
        _server.Map("POST", "/login/oauth/access_token", _ => FakeResponse.Json(answers[Math.Min(i++, answers.Length - 1)]));
    }

    [Fact]
    public void Client_id_is_empty_in_this_build() => Assert.False(GitHubDeviceFlow.IsConfigured);

    [Fact]
    public async Task Start_requests_a_device_code()
    {
        _server.Map("POST", "/login/device/code", FakeResponse.Json(new
        {
            device_code = "dev-code", user_code = "ABCD-1234", verification_uri = "https://github.com/login/device",
            expires_in = 899, interval = 5,
        }));

        var code = await Flow().StartAsync();

        Assert.Equal(new GitHubDeviceFlow.DeviceCode("dev-code", "ABCD-1234", "https://github.com/login/device", 899, 5), code);
        var r = Assert.Single(_server.Requests);
        Assert.Equal("application/json", r.Header("Accept"));
        Assert.Equal("client_id=client-123&scope=repo+user+notifications+workflow", r.Body);
    }

    [Fact]
    public async Task Poll_waits_through_pending_and_slow_down_until_the_token()
    {
        Answers(new { error = "authorization_pending" }, new { error = "slow_down" }, new { error = "authorization_pending" },
            new { access_token = "gho_TOKEN", token_type = "bearer", scope = "repo" });

        var token = await Flow().PollAsync(Code);

        Assert.Equal("gho_TOKEN", token);
        Assert.Equal([5, 5, 10, 10], _waits.Select(w => (int)w.TotalSeconds));
        var r = _server.Requests.First();
        Assert.Equal("client_id=client-123&device_code=dev-code&grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Adevice_code", r.Body);
        Assert.Equal("application/json", r.Header("Accept"));
    }

    [Fact]
    public async Task Slow_down_uses_the_returned_interval()
    {
        Answers(new { error = "slow_down", interval = 12 }, new { access_token = "t" });
        await Flow().PollAsync(Code);
        Assert.Equal([5, 12], _waits.Select(w => (int)w.TotalSeconds));
    }

    [Fact]
    public async Task Expired_code_fails_with_the_description()
    {
        Answers(new { error = "authorization_pending" },
            new { error = "expired_token", error_description = "The device_code has expired." });
        var e = await Assert.ThrowsAsync<HostingException>(() => Flow().PollAsync(Code));
        Assert.Equal("The device_code has expired.", e.Message);
    }

    [Fact]
    public async Task Denied_fails_with_a_clear_text()
    {
        Answers(new { error = "access_denied" });
        var e = await Assert.ThrowsAsync<HostingException>(() => Flow().PollAsync(Code));
        Assert.Equal("The login was denied on GitHub.", e.Message);
    }

    [Fact]
    public async Task Other_errors_fail()
    {
        Answers(new { error = "incorrect_client_credentials" });
        var e = await Assert.ThrowsAsync<HostingException>(() => Flow().PollAsync(Code));
        Assert.Equal("GitHub Error: incorrect_client_credentials", e.Message);
    }

    [Fact]
    public async Task Cancel_stops_polling()
    {
        Answers(new { error = "authorization_pending" });
        using var cts = new CancellationTokenSource();
        int polls = 0;
        var flow = Flow((_, ct) =>
        {
            if (++polls == 3) cts.Cancel();
            return Task.Delay(TimeSpan.Zero, ct);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => flow.PollAsync(Code, cts.Token));
        Assert.Equal(2, _server.Requests.Count);
    }
}
