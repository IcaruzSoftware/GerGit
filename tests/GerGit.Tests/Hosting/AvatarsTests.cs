using System.Net;
using System.Net.Sockets;
using GerGit.Hosting;

namespace GerGit.Tests.Hosting;

/// <summary>The download seam is process-wide: these tests run alone.</summary>
[CollectionDefinition("AvatarDownload", DisableParallelization = true)]
public sealed class AvatarDownloadCollection;

/// <summary>Avatars.</summary>
[Collection("AvatarDownload")]
public sealed class AvatarsTests : IDisposable
{
    readonly Func<Uri, CancellationToken, Task<byte[]?>> _download = Avatars.Download;

    public void Dispose() => Avatars.Download = _download;

    [Theory]
    [InlineData("12345+octo@users.noreply.github.com", "https://avatars.githubusercontent.com/octo")]
    [InlineData("Octo@Users.NoReply.GitHub.com", "https://avatars.githubusercontent.com/octo")]
    // md5("jane.doe@example.com"): the e-mail is trimmed and lower-cased first
    [InlineData(" Jane.Doe@Example.COM ", "https://en.gravatar.com/avatar/0cba00ca3da1b283a57287bcceb17e35?d=404")]
    public void Source_URL(string email, string expected) => Assert.Equal(expected, Avatars.SourceUrl(email)!.AbsoluteUri);

    [Fact]
    public void GitHub_logo_and_empty_mail_have_no_download()
    {
        Assert.True(Avatars.IsGitHubLogo("noreply@github.com"));
        Assert.Null(Avatars.SourceUrl("NoReply@GitHub.com"));
        Assert.Null(Avatars.SourceUrl(""));
    }

    [Theory]
    [InlineData("John Ronald Tolkien", "JT")]
    [InlineData("john ronald tolkien", "jr")]
    [InlineData("Linus", "L")]
    [InlineData("linus", "l")]
    [InlineData("  ", "?")]
    [InlineData("", "?")]
    public void Initials(string name, string expected) => Assert.Equal(expected, Avatars.Initials(name));

    [Fact]
    public void Gradient_index_is_stable_and_case_insensitive()
    {
        // FNV-1a % 5, fixed values: they must never change between runs or .NET versions.
        Assert.Equal(4, Avatars.GradientIndex("a@b.c"));
        Assert.Equal(4, Avatars.GradientIndex("A@B.C"));
        Assert.Equal(1, Avatars.GradientIndex("Jane.Doe@example.com"));
        Assert.Equal(0, Avatars.GradientIndex("x"));
        Assert.Equal(("#379FEF", "#75D4FA"), Avatars.Gradients[0]);
        Assert.Equal(5, Avatars.Gradients.Length);
    }

    [Fact]
    public async Task Cache_shares_downloads_caches_failures_and_evicts_after_128()
    {
        var calls = new List<string>();
        Avatars.Download = (url, _) =>
        {
            lock (calls) calls.Add(url.AbsoluteUri);
            return Task.FromResult(url.AbsoluteUri.Contains("missing") ? null : (byte[]?)[1, 2, 3]);
        };
        var cache = new AvatarCache<string>(bytes => bytes.Length == 3 ? "image" : null);

        var a = cache.Get("a", new Uri("https://x/a"));
        Assert.Same(a, cache.Get("a", new Uri("https://x/a")));
        Assert.Equal("image", await a);
        Assert.Null(await cache.Get("m", new Uri("https://x/missing")));
        Assert.Null(await cache.Get("m", new Uri("https://x/missing"))); // failure cached
        Assert.Null(await cache.Get("none", null));
        Assert.Equal(["https://x/a", "https://x/missing"], calls);

        for (int i = 0; i < 128; i++) await cache.Get($"k{i}", new Uri($"https://x/{i}"));
        calls.Clear();
        await cache.Get("a", new Uri("https://x/a")); // evicted (least recently used)
        await cache.Get("k127", new Uri("https://x/127")); // still cached
        Assert.Equal(["https://x/a"], calls);
    }

    [Fact]
    public async Task Undecodable_image_is_a_cached_failure()
    {
        Avatars.Download = (_, _) => Task.FromResult<byte[]?>([0]);
        var cache = new AvatarCache<string>(_ => throw new FormatException("bad image"));
        Assert.Null(await cache.Get("k", new Uri("https://x/k")));
    }

    [Fact]
    public async Task Download_drops_a_picture_larger_than_2_MB()
    {
        using var server = new FakeHostingServer();
        server.Map("GET", "/small.png", new FakeResponse(200, "picture", "image/png"));
        server.Map("GET", "/big.png", new FakeResponse(200, new string('x', Avatars.MaxBytes + 1), "image/png"));
        Assert.Equal("picture"u8.ToArray(), await Avatars.DownloadAsync(new Uri(server.Url + "/small.png"), default));
        Assert.Null(await Avatars.DownloadAsync(new Uri(server.Url + "/big.png"), default));
    }

    /// <summary>A body without a length that never ends.</summary>
    sealed class EndlessStream : Stream
    {
        public long BytesRead;
        public override int Read(byte[] buffer, int offset, int count)
        {
            BytesRead += count;
            return count;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Body_reading_stops_once_the_limit_is_passed()
    {
        var endless = new EndlessStream();
        using var content = new StreamContent(endless);
        Assert.Null(content.Headers.ContentLength);
        Assert.Null(await HostingClient.ReadBodyAsync(content, 1000, default));
        Assert.InRange(endless.BytesRead, 1001, 1000 + 81920);

        using var announced = new ByteArrayContent(new byte[11]);
        Assert.Null(await HostingClient.ReadBodyAsync(announced, 10, default));
        using var exact = new ByteArrayContent(new byte[10]);
        Assert.Equal(10, (await HostingClient.ReadBodyAsync(exact, 10, default))!.Length);
    }

    /// <summary>A server that sends the headers of a 100-byte body and then nothing.</summary>
    static (TcpListener Listener, string Url) StallingServer()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    var stream = client.GetStream();
                    await stream.ReadAtLeastAsync(new byte[8192], 1, throwOnEndOfStream: false); // the request; its content does not matter
                    await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100\r\n\r\n{"u8.ToArray());
                }
            }
            catch (Exception) { } // stopped
        });
        return (listener, $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}");
    }

    [Fact]
    public async Task A_body_that_never_ends_times_out()
    {
        var (listener, url) = StallingServer();
        var timeout = HostingClient.BodyTimeout;
        HostingClient.BodyTimeout = TimeSpan.FromMilliseconds(500);
        try
        {
            var e = await Assert.ThrowsAsync<HostingException>(() => new GitLabClient(url, "token").GetUserAsync());
            Assert.StartsWith("Error: The server took longer than ", e.Message);
            Assert.Null(await Avatars.DownloadAsync(new Uri(url + "/a.png"), default));
        }
        finally
        {
            HostingClient.BodyTimeout = timeout;
            listener.Stop();
        }
    }

    [Fact]
    public async Task Plain_http_avatar_does_not_follow_a_redirect_to_another_host()
    {
        using var server = new FakeHostingServer();
        using var other = new FakeHostingServer();
        other.Map("GET", "/b.png", new FakeResponse(200, "other", "image/png"));
        server.Map("GET", "/a.png", new FakeResponse(200, "own", "image/png"));
        server.Map("GET", "/same", new FakeResponse(302, Headers: new Dictionary<string, string> { ["Location"] = "/a.png" }));
        server.Map("GET", "/away", new FakeResponse(302, Headers: new Dictionary<string, string> { ["Location"] = other.Url + "/b.png" }));

        Assert.Equal("own"u8.ToArray(), await Avatars.DownloadAsync(new Uri(server.Url + "/same"), default));
        Assert.Null(await Avatars.DownloadAsync(new Uri(server.Url + "/away"), default));
        Assert.Empty(other.Requests);
    }
}
