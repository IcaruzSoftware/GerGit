using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GerGit.Hosting;

/// <summary>Avatars, the pure parts plus the download seam.</summary>
public static partial class Avatars
{
    /// <summary>The five placeholder gradients (top colour, bottom colour) behind the initials.</summary>
    public static readonly (string Top, string Bottom)[] Gradients =
    [
        ("#379FEF", "#75D4FA"), ("#D272E8", "#DFA3F1"), ("#F9A968", "#FBCB78"), ("#FA546B", "#F98963"), ("#58CA6B", "#AADC91"),
    ];

    /// <summary>The one network call of every avatar download (plain GET, no authorization). Returns null on failure
    /// (404 is not logged). Tests replace it so no real download starts.</summary>
    public static Func<Uri, CancellationToken, Task<byte[]?>> Download { get; set; } = DownloadAsync;

    /// <summary>"Load avatars (Gravatar, GitHub, GitLab)" (Preferences, default on). Off: no avatar is downloaded at
    /// all, also not the pictures of accounts and notifications, and every author shows the placeholder.</summary>
    public static bool Enabled { get; set; } = true;

    /// <summary>Largest avatar download; a larger picture is dropped while it is read.</summary>
    internal const int MaxBytes = 2 * 1024 * 1024;

    /// <summary>`noreply@github.com` always shows the GitHub logo (no download).</summary>
    public static bool IsGitHubLogo(string email) => email.Trim().Equals("noreply@github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>Download URL for an author e-mail: GitHub noreply addresses → avatars.githubusercontent.com, else
    /// Gravatar (MD5 of the lower-case e-mail, `d=404`). Null for an empty e-mail or the GitHub logo case.</summary>
    public static Uri? SourceUrl(string email)
    {
        email = email.Trim().ToLowerInvariant();
        if (email.Length == 0 || IsGitHubLogo(email)) return null;
        if (GitHubNoReplyRegex().Match(email) is { Success: true } m)
            return new Uri("https://avatars.githubusercontent.com/" + Uri.EscapeDataString(m.Groups[1].Value));
        return new Uri($"https://en.gravatar.com/avatar/{Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(email)))}?d=404");
    }

    /// <summary>An avatar URL for the log: no user info, query or fragment.</summary>
    internal static string LogUrl(Uri url) => url.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);

    [GeneratedRegex(@"^(?:\d+\+)?(.+)@users\.noreply\.github\.com$")]
    private static partial Regex GitHubNoReplyRegex();

    /// <summary>Placeholder initials. Same rule as the copy in `CommitListPanel.Initials`.</summary>
    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return "?";
        if (words.Length >= 2 && char.IsUpper(words[0][0])) return $"{words[0][0]}{words[^1][0]}";
        if (words.Length >= 2) return $"{words[0][0]}{words[1][0]}";
        return words[0][..1];
    }

    /// <summary>Index into <see cref="Gradients"/>: FNV-1a over the lower-case e-mail (stable across runs; same as
    /// `CommitListPanel.AvatarBrush`).</summary>
    public static int GradientIndex(string email)
    {
        uint hash = 2166136261;
        foreach (var c in email.ToLowerInvariant()) hash = (hash ^ c) * 16777619;
        return (int)(hash % (uint)Gradients.Length);
    }

    internal static async Task<byte[]?> DownloadAsync(Uri url, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            using var response = await HostingClient.SendFollowingRedirectsAsync(request, null, ct, httpStaysOnOrigin: true);
            if (response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                Trace.WriteLine($"Avatar {LogUrl(url)}: HTTP {(int)response.StatusCode}");
                return null;
            }
            if (await HostingClient.ReadBodyAsync(response.Content, MaxBytes, ct) is { } bytes) return bytes;
            Trace.WriteLine($"Avatar {LogUrl(url)}: larger than {MaxBytes / 1024 / 1024} MB");
            return null;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
            Trace.WriteLine($"Avatar {LogUrl(url)}: {e.Message}");
            return null;
        }
    }
}

/// <summary>In-memory LRU (128 entries) of decoded avatars: one instance by identity (key =
/// lower-case e-mail), one by URL. Concurrent requests for a key share one download; failures are cached as null.
/// Nothing is stored on disk.</summary>
public sealed class AvatarCache<T>(Func<byte[], T?> decode) where T : class
{
    const int Capacity = 128;

    readonly Dictionary<string, LinkedListNode<(string Key, Task<T?> Image)>> _map = [];
    readonly LinkedList<(string Key, Task<T?> Image)> _order = [];

    /// <summary>The cached or started download for <paramref name="key"/>; a null URL caches "no image". A completed task
    /// means the image (or its absence) is known at once. With <see cref="Avatars.Enabled"/> off: no image, nothing
    /// downloaded, and the cache is left as it is for when avatars are turned on again.</summary>
    public Task<T?> Get(string key, Uri? url)
    {
        if (!Avatars.Enabled) return Task.FromResult<T?>(null);
        lock (_map)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                return node.Value.Image;
            }
            var image = url is null ? Task.FromResult<T?>(null) : Task.Run(() => LoadAsync(url));
            _map[key] = _order.AddFirst((key, image));
            if (_order.Count > Capacity)
            {
                _map.Remove(_order.Last!.Value.Key);
                _order.RemoveLast();
            }
            return image;
        }
    }

    async Task<T?> LoadAsync(Uri url)
    {
        try
        {
            return await Avatars.Download(url, CancellationToken.None) is { } bytes ? decode(bytes) : null;
        }
        catch (Exception e)
        {
            Trace.WriteLine($"Avatar {Avatars.LogUrl(url)}: {e.Message}"); // e.g. an undecodable image
            return null;
        }
    }
}
