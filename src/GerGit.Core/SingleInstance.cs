using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using GerGit.Core.Git;

namespace GerGit.Core;

/// <summary>
/// One GerGit per user (Command line). A start first connects to a per-user Unix socket;
/// when a running instance answers, it gets the request and the new process exits. Otherwise the start becomes the
/// instance and listens. Protocol: one UTF-8 line per connection, `gergit/1 activate` or `gergit/1 open &lt;absolute
/// path&gt;`; the instance answers `ok` (queued for the UI) or `error` and closes. The path is data only: the UI opens
/// it like any other path (it must be a repository), nothing is run from it.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const string Prefix = "gergit/1 ";
    internal const int MaxLine = 8192; // PATH_MAX is 4096 bytes
    static readonly TimeSpan ReplyTimeout = TimeSpan.FromSeconds(2), LockTimeout = TimeSpan.FromSeconds(5);
    static readonly UTF8Encoding Strict = new(false, throwOnInvalidBytes: true);

    /// <summary>How long a client may take for its line (tests shorten or lengthen it).</summary>
    internal static TimeSpan ReadTimeout { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The instance of this process (set by Program), else null.</summary>
    public static SingleInstance? Current { get; set; }

    readonly Socket _listener;
    readonly string _path;
    readonly object _gate = new();
    readonly List<string?> _pending = [];
    Action<string?>? _handler;
    int _disposed;

    SingleInstance(Socket listener, string path)
    {
        (_listener, _path) = (listener, path);
        _ = AcceptLoopAsync();
    }

    /// <summary>$GERGIT_CONFIG_DIR/run/gergit-&lt;host&gt;.sock when that is set (tests and isolated runs never reach
    /// the user's GerGit), else $XDG_RUNTIME_DIR/gergit.sock when that folder is private (0700, ours, no symlink, in a
    /// folder only its owner can write), else &lt;config dir&gt;/run/gergit-&lt;host&gt;.sock (created 0700, must be
    /// private; the host name because a home folder shared over NFS must not reach another host's GerGit). As root
    /// see <see cref="Askpass.RuntimeDir(out bool)"/>.</summary>
    [UnsupportedOSPlatform("windows")]
    public static string DefaultSocketPath()
    {
        bool inConfigDir = true;
        var dir = Environment.GetEnvironmentVariable("GERGIT_CONFIG_DIR") is { Length: > 0 }
            ? Askpass.RunDir(ConfigFolder.Location)
            : Askpass.RuntimeDir(out inConfigDir);
        return inConfigDir ? Askpass.HostSocketPath(dir, "") : Path.Combine(dir, "gergit.sock");
    }

    /// <summary>Forwards <paramref name="request"/> (an absolute path, null = just activate; a path that cannot be sent
    /// is logged and sent as activate) to a running instance and returns null, or becomes the instance and returns it.
    /// Throws when neither works (no usable lock file, a running instance that does not answer `ok` within the reply
    /// timeout, any connect error but "nobody there"): the caller then starts on its own, without single instance.
    /// A lock file serialises starts and the shutdown, so a stale socket file is only removed while no other start can
    /// be between its bind and listen.</summary>
    [UnsupportedOSPlatform("windows")]
    public static SingleInstance? Start(string socketPath, string? request)
    {
        var line = Format(request);
        using var lockFile = Lock(socketPath + ".lock");
        if (Forward(socketPath, line)) return null;
        File.Delete(socketPath); // stale: refused or missing while we hold the lock
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(socketPath)); // atomic: fails if the file exists
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen();
        }
        catch
        {
            listener.Dispose();
            throw;
        }
        return new SingleInstance(listener, socketPath);
    }

    /// <summary>Delivers the requests from now on, first those that came before. The handler is called under a lock in
    /// arrival order (on a socket thread): it must only queue the work, e.g. post it to the UI thread.</summary>
    public void Attach(Action<string?> handler)
    {
        lock (_gate)
        {
            _handler = handler;
            foreach (var r in _pending) handler(r);
            _pending.Clear();
        }
    }

    /// <summary>Stops listening; .NET removes the socket file it bound. Under the lock, so a start that already found
    /// the socket refused cannot bind its own file before ours is gone (that would remove the new one). Idempotent.</summary>
    public void Dispose()
    {
        lock (_gate) // after this no request is accepted any more (Deliver)
        {
            if (_disposed == 1) return;
            _disposed = 1;
        }
        if (OperatingSystem.IsWindows())
        {
            _listener.Dispose();
            return;
        }
        try
        {
            using var _ = Lock(_path + ".lock");
            _listener.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _listener.Dispose();
        }
    }

    /// <summary>The line for <paramref name="request"/>; a path that cannot be sent becomes "activate" (logged).</summary>
    internal static string Format(string? request)
    {
        if (request is null) return Prefix + "activate";
        if (TryParse(Prefix + "open " + request, out _)) return Prefix + "open " + request;
        Trace.WriteLine($"Single instance: '{request}' cannot be forwarded, only activating");
        return Prefix + "activate";
    }

    /// <summary>Only well-formed lines: the version prefix, then `activate`, or `open` with an absolute path without
    /// control characters.</summary>
    internal static bool TryParse(string line, out string? path)
    {
        path = null;
        if (line == Prefix + "activate") return true;
        if (!line.StartsWith(Prefix + "open ", StringComparison.Ordinal)) return false;
        path = line[(Prefix.Length + "open ".Length)..];
        return Path.IsPathFullyQualified(path) && !path.Any(char.IsControl);
    }

    /// <summary>The exclusive lock file (flock through FileShare.None), retried until <see cref="LockTimeout"/>. A
    /// symlink is refused (.NET has no O_NOFOLLOW; the folder is private, so nobody else can swap it in between).</summary>
    [UnsupportedOSPlatform("windows")]
    static FileStream Lock(string path)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException($"'{path}' is a symlink");
        var sw = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(path, new FileStreamOptions
                {
                    Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None,
                    UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
                });
            }
            catch (IOException) when (sw.Elapsed < LockTimeout)
            {
                Thread.Sleep(10);
            }
        }
    }

    /// <summary>False when nobody is there: no file (.NET reports ENOENT as AddressNotAvailable) or refused (a stale
    /// file). True when the instance answered `ok`. Anything else throws, so a live socket is never removed.</summary>
    static bool Forward(string socketPath, string line)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        // Before Connect: on Linux SO_SNDTIMEO also bounds a connect that waits for a full backlog.
        socket.SendTimeout = socket.ReceiveTimeout = (int)ReplyTimeout.TotalMilliseconds;
        try { socket.Connect(new UnixDomainSocketEndPoint(socketPath)); }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionRefused or SocketError.AddressNotAvailable)
        {
            return false;
        }
        socket.Send(Strict.GetBytes(line + "\n"));
        var reply = new byte[16];
        int n = 0;
        while (n < reply.Length && (n == 0 || reply[n - 1] != '\n'))
        {
            int r = socket.Receive(reply, n, reply.Length - n, SocketFlags.None);
            if (r == 0) break;
            n += r;
        }
        var answer = Encoding.ASCII.GetString(reply, 0, n);
        return answer == "ok\n" ? true : throw new IOException($"The running GerGit answered '{answer.TrimEnd()}'");
    }

    async Task AcceptLoopAsync()
    {
        while (true)
        {
            Socket client;
            try { client = await _listener.AcceptAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e)
            {
                if (Volatile.Read(ref _disposed) == 1) return;
                Trace.WriteLine($"Single-instance accept failed: {e.Message}"); // e.g. EMFILE: try again
                await Task.Delay(100);
                continue;
            }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    async Task ServeAsync(Socket client)
    {
        try
        {
            using (client)
            {
                string? path = null;
                var ok = await ReadLineAsync(client) is { } line && TryParse(line, out path) && Deliver(path);
                await client.SendAsync(Encoding.ASCII.GetBytes(ok ? "ok\n" : "error\n"));
            }
        }
        catch (Exception e) // timeout, the client went away
        {
            Trace.WriteLine($"Single-instance request failed: {e.Message}");
        }
    }

    /// <summary>One line of at most <see cref="MaxLine"/> bytes within <see cref="ReadTimeout"/>; null = no line, a
    /// longer one, bytes after it, or not UTF-8.</summary>
    static async Task<string?> ReadLineAsync(Socket client)
    {
        var buffer = new byte[MaxLine];
        int n = 0;
        using var cts = new CancellationTokenSource(ReadTimeout);
        while (n < buffer.Length)
        {
            int r = await client.ReceiveAsync(buffer.AsMemory(n), SocketFlags.None, cts.Token);
            if (r == 0) return null;
            int newline = Array.IndexOf(buffer, (byte)'\n', n, r);
            n += r;
            if (newline < 0) continue;
            try { return newline == n - 1 ? Strict.GetString(buffer, 0, newline) : null; }
            catch (DecoderFallbackException) { return null; }
        }
        return null;
    }

    /// <summary>False once disposed: the start then gets `error` and opens its own window instead of an `ok` nobody
    /// acts on.</summary>
    bool Deliver(string? path)
    {
        lock (_gate)
        {
            if (_disposed == 1) return false;
            if (_handler is null) _pending.Add(path);
            else _handler(path);
            return true;
        }
    }
}
