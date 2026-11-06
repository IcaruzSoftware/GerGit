using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using GerGit.Controls;
using GerGit.Core.Git;
using GerGit.Hosting;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Avatars through the app's loader and views. The download seam is process-wide, so these tests share
/// the non-parallel collection of the Hosting avatar tests; every test uses fresh e-mails because the caches are static.</summary>
[Collection("AvatarDownload")]
public sealed class AvatarTests : IDisposable
{
    readonly Func<Uri, CancellationToken, Task<byte[]?>> _download = Avatars.Download;
    readonly ConcurrentDictionary<string, int> _calls = new();

    public void Dispose() => Avatars.Download = _download;

    static string NewEmail() => $"u{Guid.NewGuid():N}@example.com";

    void Fake(Func<Uri, Task<byte[]?>> answer) => Avatars.Download = (url, _) =>
    {
        _calls.AddOrUpdate(url.AbsoluteUri, 1, (_, n) => n + 1);
        return answer(url);
    };

    int Calls(Uri? url) => url is not null && _calls.TryGetValue(url.AbsoluteUri, out var n) ? n : 0;

    /// <summary>A 4 x 4 PNG in one colour.</summary>
    static byte[] Png(Color color)
    {
        using var bitmap = new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        using (var fb = bitmap.Lock())
            for (int y = 0; y < 4; y++)
                for (int x = 0; x < 4; x++)
                    Marshal.WriteInt32(fb.Address, y * fb.RowBytes + x * 4, unchecked((int)color.ToUInt32()));
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        return stream.ToArray();
    }

    static async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(15)) throw new TimeoutException("condition not met");
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(10);
        }
    }

    static Color Pixel(WriteableBitmap frame, Point p)
    {
        using var fb = frame.Lock();
        var at = (int)p.Y * fb.RowBytes + (int)p.X * 4;
        byte B(int i) => Marshal.ReadByte(fb.Address, at + i);
        return fb.Format == PixelFormat.Rgba8888 ? Color.FromArgb(B(3), B(0), B(1), B(2)) : Color.FromArgb(B(3), B(2), B(1), B(0));
    }

    static void AssertNear(Color expected, Color actual) =>
        Assert.True(Math.Abs(expected.R - actual.R) <= 12 && Math.Abs(expected.G - actual.G) <= 12 && Math.Abs(expected.B - actual.B) <= 12,
            $"expected ~{expected}, got {actual}");

    [AvaloniaFact]
    public async Task Unknown_mail_keeps_the_placeholder_and_the_failure_is_cached()
    {
        Fake(_ => Task.FromResult<byte[]?>(null));
        var email = NewEmail();
        var view = new AvatarView
        {
            Width = 42, Height = 42, AuthorName = "Jane Doe", Email = email,
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        var window = new Window { Width = 100, Height = 100, Content = view };
        window.Show();

        Assert.Null(await AvatarImages.ForEmail(email));
        Assert.Null(await AvatarImages.ForEmail(email.ToUpperInvariant())); // same key: lower-case e-mail
        Dispatcher.UIThread.RunJobs();
        Assert.Null(view.Image);
        Assert.Equal(1, Calls(Avatars.SourceUrl(email)));

        // Placeholder: the gradient picked by the e-mail, its top colour at the top edge.
        using (var frame = window.CaptureRenderedFrame()!)
            AssertNear(Color.Parse(Avatars.Gradients[Avatars.GradientIndex(email)].Top), Pixel(frame, new Point(21, 1)));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Picture_arrives_later_and_the_list_and_details_show_it()
    {
        var gate = new TaskCompletionSource();
        var red = Png(Color.FromRgb(255, 0, 0));
        Fake(async _ =>
        {
            await gate.Task;
            return red;
        });
        using var repo = new TempRepo();
        var email = NewEmail();
        repo.Git("config", "user.email", email);
        var sha = repo.Commit("first", 10, ("a.txt", "1"));

        using var tab = new RepoTabViewModel(await GitRepository.OpenAsync(repo.Path));
        await tab.CommitList.LoadAsync(await tab.Repo.ReadRefsAsync(), await tab.Repo.ReadStashesAsync());
        var list = new CommitListView { DataContext = tab.CommitList };
        var window = new Window { Width = 1000, Height = 300, Content = list };
        window.Show();
        var panel = list.FindControl<CommitListPanel>("List")!;
        await WaitFor(() => tab.CommitList.GetHeader(0) is not null);

        // Single-row layout: the 16 px avatar sits right of column 0, 1.5 px into its 19 px column, 3.5 px down.
        double col0End = panel.Bounds.Width - 4 - 4 - 129 - 70 - 120 - 19;
        var centre = panel.TranslatePoint(new Point(col0End + 1.5 + 8, 3.5 + 8), window)!.Value;
        var pending = AvatarImages.ForEmail(email);
        Assert.False(pending.IsCompleted);
        using (var frame = window.CaptureRenderedFrame()!)
            Assert.NotEqual(Colors.Red, Pixel(frame, centre)); // placeholder while loading

        gate.SetResult();
        // Wait on the effect: the redraw that WhenLoaded posts after the download (no race with the posted job).
        await WaitFor(() =>
        {
            using var frame = window.CaptureRenderedFrame()!;
            return Pixel(frame, centre) == Colors.Red;
        });
        Assert.NotNull(pending.Result);

        // Details pane: the same key is a cache hit, the 42 px author avatar has the picture at once.
        await tab.Details.ShowAsync([sha]);
        var details = new DetailsView { DataContext = tab.Details };
        var detailsWindow = new Window { Width = 1000, Height = 600, Content = details };
        detailsWindow.Show();
        Dispatcher.UIThread.RunJobs();
        Assert.Same(pending.Result, details.FindControl<AvatarView>("AuthorAvatar")!.Image);
        Assert.Equal(1, Calls(Avatars.SourceUrl(email)));
        detailsWindow.Close();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Concurrent_requests_share_one_download()
    {
        var gate = new TaskCompletionSource();
        Fake(async _ =>
        {
            await gate.Task;
            return Png(Colors.Blue);
        });
        var email = NewEmail();
        var requests = Enumerable.Range(0, 10).Select(i => AvatarImages.ForEmail(i % 2 == 0 ? email : email.ToUpperInvariant())).ToList();
        var views = Enumerable.Range(0, 3).Select(_ => new AvatarView { Email = email }).ToList();
        Assert.All(requests, r => Assert.Same(requests[0], r));
        gate.SetResult();
        Assert.NotNull(await requests[0]);
        await WaitFor(() => views.All(v => v.Image is not null));
        Assert.Equal(1, Calls(Avatars.SourceUrl(email)));
    }

    [AvaloniaFact]
    public async Task Identity_cache_keeps_the_last_128_mails()
    {
        Fake(_ => Task.FromResult<byte[]?>(null));
        var first = NewEmail();
        await AvatarImages.ForEmail(first);
        var others = Enumerable.Range(0, 128).Select(_ => NewEmail()).ToList();
        foreach (var e in others) await AvatarImages.ForEmail(e);
        await AvatarImages.ForEmail(first); // evicted: downloaded again
        await AvatarImages.ForEmail(others[^1]); // still cached
        Assert.Equal(2, Calls(Avatars.SourceUrl(first)));
        Assert.Equal(1, Calls(Avatars.SourceUrl(others[^1])));
    }

    [AvaloniaFact]
    public async Task Sources_GitHub_noreply_Gravatar_and_the_GitHub_logo()
    {
        Fake(_ => Task.FromResult<byte[]?>(null));
        var user = "octo" + Guid.NewGuid().ToString("N")[..8];
        await AvatarImages.ForEmail($"12345+{user}@users.noreply.github.com");
        var gravatar = NewEmail();
        await AvatarImages.ForEmail(gravatar);
        Assert.Null(await AvatarImages.ForEmail("noreply@github.com"));
        Assert.Null(await AvatarImages.ForEmail("NoReply@GitHub.com"));

        Assert.Equal(2, _calls.Count);
        Assert.Contains($"https://avatars.githubusercontent.com/{user}", _calls.Keys);
        Assert.Contains(_calls.Keys, u => u.StartsWith("https://en.gravatar.com/avatar/") && u.EndsWith("?d=404"));
        Assert.DoesNotContain(_calls.Keys, u => u.Contains("github.com/noreply") || !u.StartsWith("https://"));
    }

    [AvaloniaFact]
    public void GitHub_noreply_draws_the_GitHub_mark()
    {
        var view = new AvatarView
        {
            Width = 42, Height = 42, AuthorName = "GitHub", Email = "noreply@github.com",
            HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
        };
        var window = new Window { Width = 100, Height = 100, Content = view };
        window.Show();
        using (var frame = window.CaptureRenderedFrame()!)
        {
            AssertNear(Color.Parse("#24292F"), Pixel(frame, new Point(2, 21)));  // the square, left of the mark
            AssertNear(Colors.White, Pixel(frame, new Point(21, 8)));            // the mark's ring at the top
        }
        window.Close();
    }

    [AvaloniaFact]
    public void By_URL_binding_without_a_value_draws_no_placeholder()
    {
        Fake(_ => Task.FromResult<byte[]?>(null));
        // Url bound to a null value and to a path that does not resolve (e.g. the Accounts header with no selection).
        var views = new[] { "Null", "Missing.Path" }.Select(path => new AvatarView
        {
            Width = 32, Height = 32, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            [!AvatarView.UrlProperty] = new Avalonia.Data.Binding(path),
        }).ToList();
        var panel = new StackPanel { DataContext = new { Null = (string?)null }, Children = { views[0], views[1] } };
        var window = new Window { Width = 100, Height = 100, Content = panel };
        window.Show();
        Dispatcher.UIThread.RunJobs();

        using var frame = window.CaptureRenderedFrame()!;
        var background = Pixel(frame, new Point(80, 80));
        Assert.Equal(background, Pixel(frame, new Point(16, 16)));
        Assert.Equal(background, Pixel(frame, new Point(16, 48)));
        Assert.All(views, v => Assert.Null(v.Image));
        window.Close();
    }

    [AvaloniaFact]
    public async Task By_URL_shows_the_picture_and_nothing_without_a_URL()
    {
        Fake(url => Task.FromResult(url.AbsoluteUri.Contains("missing") ? null : (byte[]?)Png(Colors.Green)));
        var url = $"https://avatars.example.test/{Guid.NewGuid():N}.png";
        var view = new AvatarView { Url = url, Width = 32, Height = 32 };
        await WaitFor(() => view.Image is not null);

        var missing = $"https://avatars.example.test/missing-{Guid.NewGuid():N}.png";
        Assert.Null(await AvatarImages.ForUrl(missing));
        Assert.Null(await AvatarImages.ForUrl(missing)); // failure cached
        Assert.Null(new AvatarView { Url = null }.Image);
        Assert.Null(await AvatarImages.ForUrl("file:///etc/passwd")); // only http(s) is fetched
        Assert.Equal(1, Calls(new Uri(url)));
        Assert.Equal(1, Calls(new Uri(missing)));
        Assert.Equal(2, _calls.Count);
    }

    [AvaloniaFact]
    public async Task Avatar_setting_off_downloads_nothing_and_shows_the_placeholder()
    {
        Fake(_ => Task.FromResult<byte[]?>(Png(Colors.Green)));
        var email = NewEmail();
        var url = $"https://avatars.example.test/{Guid.NewGuid():N}.png";
        try
        {
            AppPreferences.LoadAvatars = false;
            Assert.Null(await AvatarImages.ForEmail(email));
            Assert.Null(await AvatarImages.ForUrl(url));
            var view = new AvatarView { Width = 42, Height = 42, AuthorName = "Jane Doe", Email = email };
            Dispatcher.UIThread.RunJobs();
            Assert.Null(view.Image);
            Assert.Empty(_calls);

            AppPreferences.LoadAvatars = true; // nothing was cached while off: the pictures load now
            Assert.NotNull(await AvatarImages.ForEmail(email));
            Assert.NotNull(await AvatarImages.ForUrl(url));
            Assert.Equal(1, Calls(Avatars.SourceUrl(email)));
            Assert.Equal(1, Calls(new Uri(url)));

            AppPreferences.LoadAvatars = false; // downloaded pictures are not shown either
            Assert.Null(await AvatarImages.ForEmail(email));
        }
        finally
        {
            Settings.Set<bool?>(AppPreferences.LoadAvatarsKey, null);
            Avatars.Enabled = true;
        }
    }

    [AvaloniaFact]
    public async Task By_URL_plain_http_loads_only_from_a_configured_GitLab_Server()
    {
        Fake(_ => Task.FromResult<byte[]?>(Png(Colors.Green)));
        var dir = Directory.CreateTempSubdirectory("gergit-avatar-").FullName;
        var saved = HostingService.Current;
        try
        {
            var hosting = new HostingService(new AccountStore(Path.Combine(dir, "accounts.json")), new GerGit.Tests.Hosting.FakeSecretStore());
            hosting.Store.Add(new Account(HostType.GitLabServer, AuthType.AccessToken, "http://gitlab.corp:8080", "jdoe"));
            HostingService.Current = hosting;
            var own = $"http://gitlab.corp:8080/uploads/{Guid.NewGuid():N}.png";
            var other = $"http://elsewhere.test/{Guid.NewGuid():N}.png";

            Assert.NotNull(await AvatarImages.ForUrl(own));
            Assert.Null(await AvatarImages.ForUrl(other)); // e.g. saved with an account by an older version
            Assert.Equal(1, Calls(new Uri(own)));
            Assert.Equal(0, Calls(new Uri(other)));

            // The refusal is not cached: once the server is configured, the same URL loads.
            hosting.Store.Add(new Account(HostType.GitLabServer, AuthType.AccessToken, "http://elsewhere.test", "jdoe"));
            Assert.NotNull(await AvatarImages.ForUrl(other));
            Assert.Equal(1, Calls(new Uri(other)));
        }
        finally
        {
            HostingService.Current = saved;
            Directory.Delete(dir, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Picture_larger_than_4096_x_4096_is_not_decoded()
    {
        var huge = TestPng.HeaderOnly(30000, 30000);
        Assert.True(AvatarImages.TooLarge(huge)); // refused from the header, before any decoding
        Assert.False(AvatarImages.TooLarge(TestPng.HeaderOnly(4096, 4096)));
        Assert.False(AvatarImages.TooLarge(Png(Colors.Green)));
        Fake(_ => Task.FromResult<byte[]?>(huge));
        Assert.Null(await AvatarImages.ForEmail(NewEmail()));
        Assert.Single(_calls);
    }
}
