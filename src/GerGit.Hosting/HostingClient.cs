using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GerGit.Hosting;

/// <summary>A failed hosting call. Message = a short text for the user (HTTP status, service error or network
/// error); it never contains a token.</summary>
public class HostingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>User info: Username = the login the server returns (never what the user typed).</summary>
public sealed record HostedUser(string Username, string DisplayName, string? AvatarUrl, string? ProfileUrl);

/// <summary>Repository list: CloneUrl = HTTPS clone URL, Group = owner / namespace name.</summary>
public sealed record HostedRepository(string Name, string CloneUrl, string SshUrl, string Group, string? AvatarUrl);

/// <summary>A pull / merge request created through REST. Number = GitHub number / GitLab iid.</summary>
public sealed record CreatedPullRequest(long Number, string WebUrl);

public enum NotificationKind { Commit, Issue, PullRequest }

/// <summary>One entry of Notifications (GitHub notification, GitLab to-do). TargetId = issue / pull request
/// number or commit hash; WebUrl = the page in the browser; Repository = `owner/repo`; AvatarUrl = repository owner
/// (GitHub) / project or author (GitLab).</summary>
public sealed record HostedNotification(string Id, bool Unread, DateTimeOffset UpdatedAt, NotificationKind Kind,
    string Title, string TargetId, string WebUrl, string Repository, string? AvatarUrl);

/// <summary>REST client of one account. One shared <see cref="HttpClient"/> per CA file; cookies off; system proxy
/// (env vars); the stack's default timeout for the headers and <see cref="BodyTimeout"/> for the body; no retries.</summary>
public abstract class HostingClient
{
    const int PageCap = 20; // Repository list: at most 20 pages

    /// <summary>Largest API response body; a larger one fails the call while it is read.</summary>
    internal const int MaxBodyBytes = 20 * 1024 * 1024;

    /// <summary>How long reading one response body may take (the headers have the HttpClient timeout); tests shorten it.</summary>
    internal static TimeSpan BodyTimeout { get; set; } = TimeSpan.FromSeconds(100);

    static readonly ConcurrentDictionary<string, HttpClient> Clients = new();
    static readonly string UserAgent = $"GerGit {typeof(HostingClient).Assembly.GetName().Version?.ToString(3)}";

    protected HostingClient(string token, string? caFile)
    {
        Token = token;
        CaFile = string.IsNullOrEmpty(caFile) ? null : caFile;
    }

    protected string Token { get; }

    /// <summary>Extra trusted CA for this account's API calls (GitLab Server); null = system store only.</summary>
    public string? CaFile { get; }

    public abstract Task<HostedUser> GetUserAsync(CancellationToken ct = default);

    /// <summary>All repositories of the user, up to 20 pages.</summary>
    public abstract Task<IReadOnlyList<HostedRepository>> GetRepositoriesAsync(CancellationToken ct = default);

    /// <summary>Creates a pull request (GitHub) / merge request (GitLab) from <paramref name="sourceBranch"/> into
    /// <paramref name="targetBranch"/> of the repository <paramref name="slug"/> (`owner/repo`, GitLab groups nested).</summary>
    public abstract Task<CreatedPullRequest> CreatePullRequestAsync(string slug, string sourceBranch, string targetBranch,
        string title, string description, CancellationToken ct = default);

    /// <summary>The first page of the user's notifications: only commits, issues and pull /
    /// merge requests; an entry with a missing field is skipped.</summary>
    public abstract Task<IReadOnlyList<HostedNotification>> GetNotificationsAsync(CancellationToken ct = default);

    /// <summary>The client for an account type. The token comes from <see cref="SecretStoreExtensions.TokenFor"/>.</summary>
    public static HostingClient For(HostType type, string serverUrl, string token, string? caFile = null) => type switch
    {
        HostType.GitHub => new GitHubClient(token),
        HostType.GitLab or HostType.GitLabServer =>
            new GitLabClient(serverUrl, token, type == HostType.GitLabServer ? caFile : null, membershipOnly: type == HostType.GitLab),
        _ => throw new ArgumentException($"No REST client for {type}", nameof(type)),
    };

    public static HostingClient For(Account account, string token) =>
        For(account.Type, account.ServerUrl, token, account.CaFile);

    /// <summary>Adds the service's authorization header.</summary>
    protected abstract void Authorize(HttpRequestMessage request);

    /// <summary>The service text of a JSON error body, null when no known field.</summary>
    protected abstract string? ServiceError(JsonNode body);

    protected Task<JsonNode> GetAsync(string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        Authorize(request);
        return SendAsync(request, CaFile, ServiceError, Token, ct);
    }

    protected Task<JsonNode> PostJsonAsync(string url, JsonObject body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        Authorize(request);
        return SendAsync(request, CaFile, ServiceError, Token, ct);
    }

    /// <summary>Pages 1..20 of a list; a page shorter than <paramref name="pageSize"/> ends it. A missing required field in
    /// any entry fails the whole list.</summary>
    protected async Task<IReadOnlyList<T>> GetPagesAsync<T>(Func<int, string> pageUrl, int pageSize, string what,
        Func<JsonNode, T> parse, CancellationToken ct)
    {
        var all = new List<T>();
        for (int page = 1; page <= PageCap; page++)
        {
            if (await GetAsync(pageUrl(page), ct) is not JsonArray items) throw ParseError(what);
            foreach (var item in items) all.Add(parse(item ?? throw ParseError(what)));
            if (items.Count < pageSize) break;
        }
        return all;
    }

    // ---- shared request machinery ----

    /// <summary>Sends the request and returns the parsed JSON body or throws a <see cref="HostingException"/> with a
    /// short text for the user. <paramref name="secret"/> is removed from every text that could carry it.
    /// Caller cancellation stays an <see cref="OperationCanceledException"/>.</summary>
    internal static async Task<JsonNode> SendAsync(HttpRequestMessage request, string? caFile,
        Func<JsonNode, string?> serviceError, string? secret, CancellationToken ct)
    {
        using var _ = request;
        if (!request.Headers.Contains("User-Agent")) request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (!request.Headers.Contains("Accept")) request.Headers.TryAddWithoutValidation("Accept", "application/json; charset=utf-8");
        // Logged without query and user info: tokens never travel there, but nothing else is needed in the log either.
        var logUrl = request.RequestUri!.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped);
        var watch = Stopwatch.StartNew();
        HttpStatusCode status;
        string body;
        bool isJson;
        try
        {
            using var response = await SendFollowingRedirectsAsync(request, caFile, ct);
            status = response.StatusCode;
            isJson = response.Content.Headers.ContentType?.MediaType == "application/json";
            body = await ReadBodyAsync(response.Content, MaxBodyBytes, ct) is { } bytes
                ? Encoding.UTF8.GetString(bytes)
                : throw Fail($"{request.Method} {logUrl}", $"Error: the server response is larger than {MaxBodyBytes / 1024 / 1024} MB",
                    secret, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or AuthenticationException or CryptographicException or UnauthorizedAccessException
                                      || (e is OperationCanceledException && !ct.IsCancellationRequested))
        {
            var inner = e;
            while (inner.InnerException is not null) inner = inner.InnerException;
            throw Fail($"{request.Method} {logUrl} failed after {watch.ElapsedMilliseconds} ms", $"Error: {inner.Message}", secret, e);
        }
        Trace.WriteLine($"{request.Method} {logUrl} -> {(int)status} ({watch.ElapsedMilliseconds} ms)");

        if ((int)status is < 200 or > 299)
        {
            string text;
            if (status == HttpStatusCode.NotFound) text = "Not found (404)";
            else if (isJson && TryParse(body) is { } json) text = serviceError(json) ?? "Error: raw json error";
            else text = $"HTTP Error {(int)status} ({new HttpResponseMessage(status).ReasonPhrase ?? status.ToString()})";
            throw Fail($"{request.Method} {logUrl}", text, secret, null);
        }
        return TryParse(body) ?? throw Fail($"{request.Method} {logUrl}", "Cannot parse JSON in server response", secret, null);
    }

    /// <summary>Sends with redirects followed by hand (at most 10, never https → http). The handler's own redirect
    /// handling only strips `Authorization`, so `Private-Token` would reach another host: every authorization header is
    /// dropped once a redirect leaves the scheme + host + port of the original request. 301/302/303 turn a POST into a
    /// GET without body, as the HTTP stack does. <paramref name="httpStaysOnOrigin"/>: a plain http request never follows
    /// a redirect to another scheme, host or port (the redirect response itself is returned).</summary>
    internal static async Task<HttpResponseMessage> SendFollowingRedirectsAsync(HttpRequestMessage request, string? caFile,
        CancellationToken ct, bool httpStaysOnOrigin = false)
    {
        var http = Http(caFile);
        var origin = request.RequestUri!;
        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        for (int hop = 0; hop < 10 && (int)response.StatusCode is 301 or 302 or 303 or 307 or 308; hop++)
        {
            if (response.Headers.Location is not { } location) break;
            var target = new Uri(request.RequestUri!, location);
            if (target.Scheme != Uri.UriSchemeHttps && (target.Scheme != Uri.UriSchemeHttp || origin.Scheme == Uri.UriSchemeHttps)) break;
            var sameOrigin = target.Scheme == origin.Scheme && target.Host == origin.Host && target.Port == origin.Port;
            if (httpStaysOnOrigin && origin.Scheme == Uri.UriSchemeHttp && !sameOrigin) break;
            var keepMethod = (int)response.StatusCode is 307 or 308 || request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
            var next = new HttpRequestMessage(keepMethod ? request.Method : HttpMethod.Get, target)
            {
                Content = keepMethod ? request.Content : null,
            };
            foreach (var header in request.Headers)
                if (sameOrigin || !(header.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) ||
                                    header.Key.Equals("Private-Token", StringComparison.OrdinalIgnoreCase)))
                    next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            response.Dispose();
            request = next;
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        return response;
    }

    /// <summary>The body, read as a stream; null as soon as it passes <paramref name="limit"/> bytes (or announces more),
    /// so a huge or endless response is never held in memory. A body that takes longer than <see cref="BodyTimeout"/>
    /// throws an <see cref="IOException"/>; cancelling <paramref name="ct"/> stays an <see cref="OperationCanceledException"/>.</summary>
    internal static async Task<byte[]?> ReadBodyAsync(HttpContent content, int limit, CancellationToken ct)
    {
        if (content.Headers.ContentLength > limit) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(BodyTimeout);
        try
        {
            await using var stream = await content.ReadAsStreamAsync(timeout.Token);
            using var body = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = await stream.ReadAsync(chunk, timeout.Token)) > 0)
            {
                if (body.Length + n > limit) return null;
                body.Write(chunk, 0, n);
            }
            return body.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new IOException($"The server took longer than {BodyTimeout.TotalSeconds:0} seconds to send its response.");
        }
    }

    /// <summary>An avatar URL from a server response, or null: https only; plain http only on <paramref name="httpServer"/>'s
    /// own scheme, host and port (a GitLab Server without TLS). A response cannot point avatar downloads at any other host
    /// over plain http, and the download does not follow a plain http redirect to another host either.</summary>
    public static string? AvatarUrl(string? url, string? httpServer = null)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
        if (u.Scheme == Uri.UriSchemeHttps) return url;
        return u.Scheme == Uri.UriSchemeHttp && Uri.TryCreate(httpServer, UriKind.Absolute, out var server)
               && server.Scheme == Uri.UriSchemeHttp && u.Host.Equals(server.Host, StringComparison.OrdinalIgnoreCase)
               && u.Port == server.Port
            ? url
            : null;
    }

    static HostingException Fail(string what, string text, string? secret, Exception? inner)
    {
        // A very short "token" (a typo in the login window) would mangle normal words; real tokens are far longer.
        if (secret is { Length: >= 8 }) text = text.Replace(secret, "***", StringComparison.Ordinal);
        Trace.WriteLine($"{what}: {text}");
        return new HostingException(text, inner);
    }

    static JsonNode? TryParse(string body)
    {
        try { return JsonNode.Parse(body); }
        catch (JsonException) { return null; }
    }

    internal static HttpClient Http(string? caFile) => Clients.GetOrAdd(caFile ?? "", path =>
    {
        // proxy: HttpClient.DefaultProxy (env vars); redirects: SendFollowingRedirectsAsync
        var handler = new SocketsHttpHandler { UseCookies = false, AllowAutoRedirect = false };
        if (path.Length > 0)
        {
            var authorities = LoadCertificates(path);
            handler.SslOptions.RemoteCertificateValidationCallback =
                (_, certificate, chain, errors) => Validate(certificate, chain, errors, authorities);
        }
        return new HttpClient(handler);
    });

    /// <summary>System chain valid, or a chain to one of the account's CAs with a matching host name.</summary>
    internal static bool Validate(X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors,
        X509Certificate2Collection authorities)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (errors != SslPolicyErrors.RemoteCertificateChainErrors || certificate is not X509Certificate2 leaf) return false;
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.AddRange(authorities);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        custom.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1")); // serverAuth
        if (chain is not null)
            foreach (var element in chain.ChainElements) custom.ChainPolicy.ExtraStore.Add(element.Certificate);
        return custom.Build(leaf);
    }

    /// <summary>A PEM bundle or one DER certificate.</summary>
    static X509Certificate2Collection LoadCertificates(string path)
    {
        var certificates = new X509Certificate2Collection();
        var text = File.ReadAllText(path);
        if (text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)) certificates.ImportFromPem(text);
        else certificates.Add(X509CertificateLoader.LoadCertificateFromFile(path));
        return certificates;
    }

    // ---- JSON field helpers ----

    internal static HostingException ParseError(string what) => new($"Parse Error: {what}. Contact support.");

    /// <summary>The field of an object node; null for a missing field or a node that is not an object.</summary>
    internal static JsonNode? Child(JsonNode? node, string field) => (node as JsonObject)?[field];

    internal static string? Str(JsonNode? node, string field) =>
        Child(node, field) is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    internal static string Required(JsonNode? node, string field, string what) => Str(node, field) ?? throw ParseError(what);

    internal static long RequiredNumber(JsonNode? node, string field, string what) =>
        Child(node, field) is JsonValue v && v.TryGetValue(out long n) ? n : throw ParseError(what);

    internal static string? Number(JsonNode? node, string field) =>
        Child(node, field) is JsonValue v && v.TryGetValue(out long n) ? n.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;

    internal static DateTimeOffset? Date(JsonNode? node, string field) =>
        DateTimeOffset.TryParse(Str(node, field), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    /// <summary>The list entries <paramref name="parse"/> accepts (null = skipped); a body that is no array is a
    /// parse error.</summary>
    internal static IReadOnlyList<T> Entries<T>(JsonNode json, string what, Func<JsonNode, T?> parse) where T : class =>
        json is JsonArray items ? [.. items.Select(i => i is null ? null : parse(i)).OfType<T>()] : throw ParseError(what);

    internal static string EscapePath(string slug) => string.Join('/', slug.Split('/').Select(Uri.EscapeDataString));
}
