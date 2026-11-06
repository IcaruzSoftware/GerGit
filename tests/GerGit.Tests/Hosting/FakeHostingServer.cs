using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using GerGit.Hosting;

namespace GerGit.Tests.Hosting;

/// <summary>A request the fake server received. Path without query; header names lower-case.</summary>
public sealed record FakeRequest(string Method, string Path, string Query, IReadOnlyDictionary<string, string> Headers, string Body)
{
    public string? Header(string name) => Headers.GetValueOrDefault(name.ToLowerInvariant());

    public string? QueryValue(string name) => Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(p => p.Split('=', 2)).Where(p => p[0] == name).Select(p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : ""))
        .FirstOrDefault();
}

public sealed record FakeResponse(int Status, string Body = "", string ContentType = "application/json; charset=utf-8",
    IReadOnlyDictionary<string, string>? Headers = null)
{
    public static FakeResponse Json(object value, int status = 200, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, JsonSerializer.Serialize(value), Headers: headers);
}

/// <summary>Fake GitHub / GitLab for tests (no real network): a tiny HTTP/1.1 server on 127.0.0.1 (port 0), optionally
/// TLS with a test certificate. Routes are `METHOD /path` (query ignored for matching); unknown routes answer 404.
/// Every request is recorded with headers and body. One request per connection (`Connection: close`).</summary>
public sealed class FakeHostingServer : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly X509Certificate2? _certificate;
    readonly ConcurrentDictionary<string, Func<FakeRequest, FakeResponse>> _routes = new();
    readonly CancellationTokenSource _stop = new();

    public FakeHostingServer(X509Certificate2? tlsCertificate = null)
    {
        _certificate = tlsCertificate;
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        BaseUri = new Uri($"{(tlsCertificate is null ? "http" : "https")}://127.0.0.1:{port}");
        _ = AcceptLoopAsync();
    }

    /// <summary>`http://127.0.0.1:&lt;port&gt;` (or https), no trailing slash in <see cref="Url"/>.</summary>
    public Uri BaseUri { get; }

    public string Url => BaseUri.AbsoluteUri.TrimEnd('/');

    public ConcurrentQueue<FakeRequest> Requests { get; } = new();

    public void Map(string method, string path, Func<FakeRequest, FakeResponse> handler) => _routes[$"{method} {path}"] = handler;

    public void Map(string method, string path, FakeResponse response) => Map(method, path, _ => response);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }

    async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; } // stopped
            _ = HandleAsync(client);
        }
    }

    async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            Stream stream = client.GetStream();
            if (_certificate is not null)
            {
                var ssl = new SslStream(stream);
                await ssl.AuthenticateAsServerAsync(_certificate);
                stream = ssl;
            }
            if (await ReadRequestAsync(stream) is not { } request) return;
            Requests.Enqueue(request);
            var response = _routes.TryGetValue($"{request.Method} {request.Path}", out var handler)
                ? handler(request)
                : FakeResponse.Json(new { message = "Not Found" }, 404);
            await WriteResponseAsync(stream, response);
        }
        catch (Exception) { } // the client gave up (e.g. rejected our certificate)
    }

    static async Task<FakeRequest?> ReadRequestAsync(Stream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int headerEnd;
        while ((headerEnd = buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf("\r\n\r\n"u8)) < 0)
        {
            int n = await stream.ReadAsync(chunk);
            if (n == 0) return null;
            buffer.Write(chunk, 0, n);
        }
        var lines = Encoding.ASCII.GetString(buffer.GetBuffer(), 0, headerEnd).Split("\r\n");
        var start = lines[0].Split(' ');
        var headers = lines.Skip(1).Select(l => l.Split(':', 2))
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());
        var length = headers.TryGetValue("content-length", out var cl) ? int.Parse(cl) : 0;
        var body = new MemoryStream();
        body.Write(buffer.GetBuffer(), headerEnd + 4, (int)buffer.Length - headerEnd - 4);
        while (body.Length < length)
        {
            int n = await stream.ReadAsync(chunk);
            if (n == 0) break;
            body.Write(chunk, 0, n);
        }
        var target = start[1];
        var q = target.IndexOf('?');
        return new FakeRequest(start[0], q < 0 ? target : target[..q], q < 0 ? "" : target[q..], headers,
            Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length));
    }

    static async Task WriteResponseAsync(Stream stream, FakeResponse response)
    {
        var body = Encoding.UTF8.GetBytes(response.Body);
        var head = new StringBuilder($"HTTP/1.1 {response.Status} {Reason(response.Status)}\r\n");
        head.Append($"Content-Type: {response.ContentType}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n");
        foreach (var (name, value) in response.Headers ?? new Dictionary<string, string>()) head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    static string Reason(int status) => new HttpResponseMessage((HttpStatusCode)status).ReasonPhrase ?? "Status";
}

/// <summary>A test CA and a server certificate for 127.0.0.1 / localhost signed by it (created once per test run).</summary>
public static class TestCertificates
{
    static readonly Lazy<X509Certificate2> TestCa = new(() => CreateCa("GerGit Test CA"));
    static readonly Lazy<X509Certificate2> TestServer = new(() => CreateServer(TestCa.Value, "localhost", includeLoopback: true));

    /// <summary>The test CA (with its private key; <see cref="WriteCaFile"/> writes only the certificate).</summary>
    public static X509Certificate2 Ca => TestCa.Value;

    /// <summary>Server certificate for 127.0.0.1 / localhost signed by <see cref="Ca"/>.</summary>
    public static X509Certificate2 Server => TestServer.Value;

    public static X509Certificate2 CreateCa(string commonName)
    {
        var now = DateTimeOffset.UtcNow;
        var key = RSA.Create(2048); // not disposed: the CA signs server certificates later
        var request = new CertificateRequest($"CN={commonName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
    }

    /// <summary>A TLS server certificate signed by <paramref name="ca"/>, issued for <paramref name="dnsName"/> and, when
    /// <paramref name="includeLoopback"/>, for 127.0.0.1 too.</summary>
    public static X509Certificate2 CreateServer(X509Certificate2 ca, string dnsName, bool includeLoopback)
    {
        var now = DateTimeOffset.UtcNow;
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        if (includeLoopback) names.AddIpAddress(IPAddress.Loopback);
        names.AddDnsName(dnsName);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        request.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(ca, true, false));
        using var signed = request.Create(ca, now.AddDays(-1), now.AddMonths(6), RandomNumberGenerator.GetBytes(8));
        using var withKey = signed.CopyWithPrivateKey(key);
        // Re-imported through PKCS#12 so SslStream on Linux can use the private key.
        return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pfx), null);
    }

    /// <summary>Writes the CA as PEM into <paramref name="dir"/> and returns the path.</summary>
    public static string WriteCaFile(string dir)
    {
        var path = Path.Combine(dir, "ca.pem");
        File.WriteAllText(path, Ca.ExportCertificatePem());
        return path;
    }
}

/// <summary>In-memory <see cref="ISecretStore"/>; <see cref="Fail"/> makes every call fail like a missing keyring.</summary>
public sealed class FakeSecretStore : ISecretStore
{
    public ConcurrentDictionary<(string Server, string User, SecretKind Kind), string> Items { get; } = new();
    public bool Fail { get; set; }

    public void Store(string server, string user, SecretKind kind, string secret)
    {
        if (Fail) throw new SecretStoreException(SecretStoreException.SaveFailed);
        Items[(server, user, kind)] = secret;
    }

    public string? Lookup(string server, string user, SecretKind kind)
    {
        if (Fail) throw new SecretStoreException(SecretStoreException.ReadFailed);
        return Items.GetValueOrDefault((server, user, kind));
    }

    public void Clear(string server, string user)
    {
        foreach (var key in Items.Keys.Where(k => k.Server == server && k.User == user)) Items.TryRemove(key, out _);
    }
}
