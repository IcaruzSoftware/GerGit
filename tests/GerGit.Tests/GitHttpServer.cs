using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace GerGit.Tests;

/// <summary>
/// Serves the bare repositories under <see cref="Root"/> over smart HTTP on 127.0.0.1 (`git http-backend` as CGI behind
/// an HttpListener), with HTTP Basic auth for one fixed user + token: anything else gets 401 + `WWW-Authenticate: Basic`.
/// Push is enabled for every repository. Clone URL: <c>Url + "name.git"</c>. No network beyond loopback.
/// </summary>
public sealed class GitHttpServer : IDisposable
{
    readonly HttpListener _listener;
    readonly string _expected;
    readonly string _user;

    /// <summary>Folder that holds the bare repositories (GIT_PROJECT_ROOT); deleted on Dispose.</summary>
    public string Root { get; } = Directory.CreateTempSubdirectory("gergit-http-").FullName;
    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}/";
    /// <summary>Requests that came with a wrong or no Authorization header.</summary>
    public int Rejected;

    public GitHttpServer(string user, string token)
    {
        _user = user;
        _expected = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{token}"));
        // The probed port can be taken before HttpListener binds it: one retry on a new port.
        for (int attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            Port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            _listener = new HttpListener();
            _listener.Prefixes.Add(Url);
            try
            {
                _listener.Start();
                break;
            }
            catch (HttpListenerException) when (attempt == 0) { _listener.Close(); }
        }
        _ = LoopAsync();
    }

    /// <summary>Creates <c>Root/name.git</c> as a copy of <paramref name="source"/> (or empty), push enabled; returns its path.</summary>
    public string AddRepo(string name, string? source = null)
    {
        var path = Path.Combine(Root, name + ".git");
        if (source is null) Git(Root, "init", "--bare", "-q", "-b", "main", path);
        else Git(Root, "clone", "--bare", "-q", source, path);
        Git(path, "config", "http.receivepack", "true");
        return path;
    }

    static void Git(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null";
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr.Result}");
    }

    async Task LoopAsync()
    {
        while (true)
        {
            HttpListenerContext c;
            try { c = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
            _ = Task.Run(() => ServeAsync(c));
        }
    }

    async Task ServeAsync(HttpListenerContext c)
    {
        try
        {
            if (c.Request.Headers["Authorization"] != _expected)
            {
                Interlocked.Increment(ref Rejected);
                c.Response.StatusCode = 401;
                c.Response.AddHeader("WWW-Authenticate", "Basic realm=\"test\"");
                c.Response.Close();
                return;
            }
            await RunBackendAsync(c);
        }
        catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or IOException)
        {
            try { c.Response.Abort(); } catch (ObjectDisposedException) { }
        }
    }

    async Task RunBackendAsync(HttpListenerContext c)
    {
        var req = c.Request;
        var psi = new ProcessStartInfo("git", "http-backend")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        var env = psi.Environment;
        env["GIT_PROJECT_ROOT"] = Root;
        env["GIT_HTTP_EXPORT_ALL"] = "1";
        env["GIT_CONFIG_GLOBAL"] = "/dev/null";
        env["GIT_CONFIG_NOSYSTEM"] = "1";
        env["REQUEST_METHOD"] = req.HttpMethod;
        env["PATH_INFO"] = Uri.UnescapeDataString(req.Url!.AbsolutePath);
        env["QUERY_STRING"] = req.Url.Query.TrimStart('?');
        env["CONTENT_TYPE"] = req.ContentType ?? "";
        if (req.ContentLength64 >= 0) env["CONTENT_LENGTH"] = req.ContentLength64.ToString();
        env["REMOTE_USER"] = _user;
        env["REMOTE_ADDR"] = "127.0.0.1";
        if (req.Headers["Content-Encoding"] is { } enc) env["HTTP_CONTENT_ENCODING"] = enc;
        if (req.Headers["Git-Protocol"] is { } proto) env["HTTP_GIT_PROTOCOL"] = proto;

        using var p = Process.Start(psi)!;
        var output = new MemoryStream();
        var stdout = p.StandardOutput.BaseStream.CopyToAsync(output);
        var stderr = p.StandardError.ReadToEndAsync();
        await req.InputStream.CopyToAsync(p.StandardInput.BaseStream);
        p.StandardInput.Close();
        await stdout;
        await stderr;
        await p.WaitForExitAsync();

        // CGI answer: header lines, a blank line, the body.
        var bytes = output.ToArray();
        int split = IndexOf(bytes, "\r\n\r\n"u8), skip = 4;
        if (split < 0) (split, skip) = (IndexOf(bytes, "\n\n"u8), 2);
        if (split < 0) (split, skip) = (0, 0);
        foreach (var line in Encoding.ASCII.GetString(bytes, 0, split).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Split(':', 2) is not [var name, var value]) continue;
            value = value.Trim();
            if (name.Equals("Status", StringComparison.OrdinalIgnoreCase)) c.Response.StatusCode = int.Parse(value[..3]);
            else if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) c.Response.ContentType = value;
            else if (!name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) c.Response.AddHeader(name, value);
        }
        var body = bytes.AsMemory(split + skip);
        c.Response.ContentLength64 = body.Length;
        await c.Response.OutputStream.WriteAsync(body);
        c.Response.Close();
    }

    static int IndexOf(byte[] data, ReadOnlySpan<byte> what) => data.AsSpan().IndexOf(what);

    public void Dispose()
    {
        _listener.Close();
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
    }
}
