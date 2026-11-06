using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>The single-instance tests change <see cref="SingleInstance.ReadTimeout"/>: never in parallel.</summary>
[CollectionDefinition("SingleInstance", DisableParallelization = true)]
public sealed class SingleInstanceCollection;

/// <summary>Command line: the single-instance socket, each test on its own socket path.</summary>
[Collection("SingleInstance")]
[SupportedOSPlatform("linux")]
public sealed class SingleInstanceTests : IDisposable
{
    const UnixFileMode Private = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "gergit-si-" + Guid.NewGuid().ToString("N")[..8]), Private).FullName;

    string Sock => Path.Combine(_dir, "gergit.sock");

    public void Dispose()
    {
        SingleInstance.ReadTimeout = TimeSpan.FromSeconds(1);
        Directory.Delete(_dir, recursive: true);
    }

    /// <summary>Collects what the instance delivers.</summary>
    static BlockingCollection<string?> Received(SingleInstance instance)
    {
        var got = new BlockingCollection<string?>();
        instance.Attach(got.Add);
        return got;
    }

    static string? Next(BlockingCollection<string?> got) =>
        got.TryTake(out var r, TimeSpan.FromSeconds(5)) ? r : throw new TimeoutException("nothing delivered");

    Socket Connect()
    {
        var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        s.Connect(new UnixDomainSocketEndPoint(Sock));
        s.ReceiveTimeout = 5000;
        return s;
    }

    /// <summary>Sends raw bytes and half-closes, returns the answer ("" = closed without one; null = reset).</summary>
    string? Raw(byte[] request)
    {
        using var s = Connect();
        try
        {
            // When the instance closes while the oversized request is only partly sent, .NET reports the broken pipe
            // of the rest as TimedOut (no send timeout is set, so it cannot be a real one).
            try { s.Send(request); }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.TimedOut) { return null; }
            try { s.Shutdown(SocketShutdown.Send); } catch (SocketException) { } // the oversized one may be cut off already
            var answer = new MemoryStream();
            var buffer = new byte[64];
            int n;
            while ((n = s.Receive(buffer)) > 0) answer.Write(buffer, 0, n);
            return Encoding.ASCII.GetString(answer.ToArray());
        }
        catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset or SocketError.Shutdown) { return null; }
    }

    [Fact]
    public void The_first_start_listens_privately_and_removes_its_socket_on_dispose()
    {
        var first = SingleInstance.Start(Sock, null);
        Assert.NotNull(first);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Sock));
        Assert.Equal("ok\n", Raw("gergit/1 activate\n"u8.ToArray()));
        first.Dispose();
        Assert.False(File.Exists(Sock));
        first.Dispose(); // twice is harmless

        using var next = SingleInstance.Start(Sock, "/r"); // after the shutdown the next start is the instance
        Assert.NotNull(next);
    }

    [Fact]
    public void A_second_start_forwards_its_path_or_activate_and_the_first_receives_it()
    {
        using var first = SingleInstance.Start(Sock, null)!;
        Assert.Null(SingleInstance.Start(Sock, "/home/u/early")); // before Attach: queued, still answered
        var got = Received(first);
        Assert.Equal("/home/u/early", Next(got));
        Assert.Null(SingleInstance.Start(Sock, "/home/u/my repo ä"));
        Assert.Equal("/home/u/my repo ä", Next(got));
        Assert.Null(SingleInstance.Start(Sock, null));
        Assert.Null(Next(got)); // null = activate only
        Assert.True(File.Exists(Sock)); // the forwarding start removed nothing
    }

    [Fact]
    public void A_stale_socket_file_is_taken_over()
    {
        // Like a crashed GerGit: the socket file stays, nobody listens. (.NET unlinks the file it bound on Dispose, so
        // bind elsewhere and move it.)
        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            dead.Bind(new UnixDomainSocketEndPoint(Sock + ".tmp"));
            File.Move(Sock + ".tmp", Sock);
        }
        Assert.True(File.Exists(Sock));

        using var instance = SingleInstance.Start(Sock, null);
        Assert.NotNull(instance);
        var got = Received(instance);
        Assert.Null(SingleInstance.Start(Sock, "/r"));
        Assert.Equal("/r", Next(got));
    }

    [Fact]
    public void Garbage_oversized_and_silent_clients_are_rejected_without_blocking_the_instance()
    {
        SingleInstance.ReadTimeout = TimeSpan.FromSeconds(60); // the silent client stays for the whole test
        using var instance = SingleInstance.Start(Sock, null)!;
        var got = Received(instance);
        using var silent = Connect(); // sends nothing and keeps the connection open

        foreach (var bad in new[]
        {
            "hello\n", "gergit/2 activate\n", "gergit/1 open relative/path\n", "gergit/1 open /a\tb\n",
            "gergit/1 open /a\ngergit/1 open /b\n", "gergit/1 activate", "\n",
        })
            Assert.Equal("error\n", Raw(Encoding.UTF8.GetBytes(bad)));
        Assert.Equal("error\n", Raw([.. "gergit/1 open /"u8, 0xFF, 0xFE, (byte)'\n'])); // not UTF-8
        Assert.Contains(Raw(Encoding.ASCII.GetBytes(new string('x', SingleInstance.MaxLine * 8))), new[] { "error\n", null });

        Assert.Null(SingleInstance.Start(Sock, "/fine")); // the silent client blocks nobody
        Assert.Equal("/fine", Next(got));
        Assert.Empty(got); // none of the garbage was delivered
    }

    [Fact]
    public void A_client_without_a_complete_line_is_closed_without_an_answer_after_the_read_timeout()
    {
        SingleInstance.ReadTimeout = TimeSpan.FromMilliseconds(200);
        using var instance = SingleInstance.Start(Sock, null)!;
        var got = Received(instance);
        using var slow = Connect();
        slow.Send("gergit/1 acti"u8.ToArray());
        Assert.Equal(0, slow.Receive(new byte[16])); // closed, no answer
        Assert.Empty(got);
    }

    [Fact]
    public void Concurrent_starts_give_exactly_one_listener()
    {
        const int n = 8;
        using var barrier = new Barrier(n);
        var results = new SingleInstance?[n];
        var threads = Enumerable.Range(0, n).Select(i => new Thread(() =>
        {
            barrier.SignalAndWait();
            results[i] = SingleInstance.Start(Sock, $"/r{i}");
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        var instance = Assert.Single(results, r => r is not null)!;
        using (instance)
        {
            var got = Received(instance);
            var paths = Enumerable.Range(0, n - 1).Select(_ => Next(got)).ToHashSet();
            Assert.Equal(n - 1, paths.Count); // every other start forwarded its own path
            Assert.DoesNotContain($"/r{Array.IndexOf(results, instance)}", paths);
        }
    }

    [Fact]
    public async Task Dispose_waits_for_a_start_that_holds_the_lock()
    {
        var instance = SingleInstance.Start(Sock, null)!;
        Task dispose;
        using (new FileStream(Sock + ".lock", FileMode.Open, FileAccess.ReadWrite, FileShare.None)) // a start deciding
        {
            dispose = Task.Run(instance.Dispose);
            await Task.Delay(300);
            Assert.False(dispose.IsCompleted); // the socket cannot vanish under a start that holds the lock
            Assert.True(File.Exists(Sock));
        }
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(Sock));
    }

    [Fact]
    public void A_request_that_arrives_during_the_shutdown_is_answered_error()
    {
        var instance = SingleInstance.Start(Sock, null)!;
        var got = Received(instance);
        using var late = Connect();
        late.Send("gergit/1 acti"u8.ToArray());
        Thread.Sleep(300); // accepted and reading before the shutdown (the read timeout is 1 s)
        instance.Dispose();
        late.Send("vate\n"u8.ToArray());
        Assert.Equal("error\n", Read(late));
        Assert.Empty(got);
    }

    static string Read(Socket s)
    {
        var answer = new MemoryStream();
        var buffer = new byte[64];
        int n;
        while ((n = s.Receive(buffer)) > 0) answer.Write(buffer, 0, n);
        return Encoding.ASCII.GetString(answer.ToArray());
    }

    [Fact]
    public async Task A_running_instance_that_answers_wrong_is_not_replaced()
    {
        using var fake = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        fake.Bind(new UnixDomainSocketEndPoint(Sock));
        fake.Listen();
        var serve = Task.Run(() =>
        {
            using var c = fake.Accept();
            c.Receive(new byte[256]);
            c.Send("nope\n"u8.ToArray());
        });
        Assert.Throws<IOException>(() => SingleInstance.Start(Sock, null)); // the caller starts without single instance
        await serve;
        Assert.True(File.Exists(Sock));
    }

    [Fact]
    public void A_connect_error_other_than_refused_or_missing_leaves_the_socket_alone()
    {
        Assert.SkipWhen(Environment.IsPrivilegedProcess, "root may connect to a 0000 socket");
        using var other = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        other.Bind(new UnixDomainSocketEndPoint(Sock));
        other.Listen();
        File.SetUnixFileMode(Sock, UnixFileMode.None); // connect needs write permission: EACCES
        var e = Assert.Throws<SocketException>(() => SingleInstance.Start(Sock, null));
        Assert.Equal(SocketError.AccessDenied, e.SocketErrorCode);
        Assert.True(File.Exists(Sock));
    }

    [Fact]
    public void A_symlinked_lock_file_is_refused()
    {
        var target = Path.Combine(_dir, "elsewhere");
        File.CreateSymbolicLink(Sock + ".lock", target);
        Assert.Throws<IOException>(() => SingleInstance.Start(Sock, null));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(Sock));
    }

    [Fact]
    public void A_path_that_cannot_be_forwarded_only_activates()
    {
        using var instance = SingleInstance.Start(Sock, "relative")!; // no throw before the lock: it becomes the instance
        var got = Received(instance);
        Assert.Null(SingleInstance.Start(Sock, "/a\nb"));
        Assert.Null(Next(got));
        Assert.Null(SingleInstance.Start(Sock, "relative"));
        Assert.Null(Next(got));
    }

    [Fact]
    public void The_default_socket_lives_in_a_private_folder_under_the_test_config_dir()
    {
        var config = Environment.GetEnvironmentVariable("GERGIT_CONFIG_DIR")!; // set for every test (TestSettings)
        Assert.Equal(Path.Combine(config, "run", $"gergit-{Environment.MachineName}.sock"), SingleInstance.DefaultSocketPath());
        Assert.Equal(Private, File.GetUnixFileMode(Path.Combine(config, "run")));
    }

    [Fact]
    public void Only_a_real_0700_folder_is_private()
    {
        var fresh = Path.Combine(_dir, "run");
        Assert.Equal(fresh, Askpass.PrivateDir(fresh));
        Assert.Equal(Private, File.GetUnixFileMode(fresh));

        var open = Directory.CreateDirectory(Path.Combine(_dir, "open"), Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute).FullName;
        Assert.Throws<IOException>(() => Askpass.PrivateDir(open));

        var shared = Directory.CreateDirectory(Path.Combine(_dir, "shared"), Private | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute).FullName;
        File.SetUnixFileMode(shared, Private | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute); // past the umask
        Assert.Throws<IOException>(() => Askpass.PrivateDir(Path.Combine(shared, "run"))); // the parent is group-writable

        var link = Path.Combine(_dir, "link");
        Directory.CreateSymbolicLink(link, fresh); // a symlink to a private folder could be swapped: refused
        Assert.Throws<IOException>(() => Askpass.PrivateDir(link));
    }

    [Fact]
    public void The_config_folder_is_created_private_and_a_writable_one_is_tightened_without_touching_its_parent()
    {
        var parent = Directory.CreateDirectory(Path.Combine(_dir, "home"), Private | UnixFileMode.GroupWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute).FullName;
        File.SetUnixFileMode(parent, Private | UnixFileMode.GroupWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);

        var fresh = Path.Combine(parent, "gergit");
        ConfigFolder.Create(fresh);
        Assert.Equal(Private, File.GetUnixFileMode(fresh));

        var loose = Directory.CreateDirectory(Path.Combine(parent, "loose")).FullName;
        File.SetUnixFileMode(loose, Private | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        ConfigFolder.Create(loose); // like a ~/.config/gergit made under umask 002
        Assert.Equal(Private, File.GetUnixFileMode(loose));
        Assert.Equal(Askpass.RunDir(loose), Path.Combine(loose, "run")); // now usable for the sockets

        var readable = Directory.CreateDirectory(Path.Combine(parent, "readable")).FullName;
        File.SetUnixFileMode(readable, Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
        ConfigFolder.Create(readable); // not writable by others: left as it is
        Assert.Equal(Private | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(readable));

        Assert.Equal(Private | UnixFileMode.GroupWrite | UnixFileMode.GroupRead | UnixFileMode.GroupExecute, File.GetUnixFileMode(parent));

        var other = Path.Combine(parent, "store", "accounts.json"); // a store with its own path: created plainly
        ConfigFolder.CreateFolderOf(other);
        Assert.True(Directory.Exists(Path.GetDirectoryName(other)));
    }

    [Fact]
    public void A_symlinked_config_folder_is_not_chmodded_and_a_writable_target_is_refused()
    {
        const UnixFileMode loose = Private | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;
        var target = Directory.CreateDirectory(Path.Combine(_dir, "dotfiles-gergit")).FullName;
        File.SetUnixFileMode(target, loose);
        var link = Path.Combine(_dir, "gergit");
        Directory.CreateSymbolicLink(link, target);

        Assert.Equal(link, ConfigFolder.Create(link));
        Assert.Equal(loose, File.GetUnixFileMode(target)); // the target keeps its mode
        Assert.Throws<IOException>(() => Askpass.RunDir(link)); // its run folder sits in a group-writable folder
        Assert.Equal(loose, File.GetUnixFileMode(target));
    }

    [Fact]
    public void A_setgid_run_folder_still_counts_as_private()
    {
        var run = Directory.CreateDirectory(Path.Combine(_dir, "run"), Private).FullName;
        File.SetUnixFileMode(run, Private | UnixFileMode.SetGroup); // 02700, inherited from a setgid parent
        Assert.Equal(run, Askpass.PrivateDir(run));
        Assert.Equal(Private | UnixFileMode.SetGroup, File.GetUnixFileMode(run)); // the owner check keeps the bit
    }

    [Fact]
    public void As_root_the_config_folder_ignores_the_users_home_and_xdg_but_not_the_test_override()
    {
        Assert.Equal("/root/.config/gergit", ConfigFolder.LocationFor(null, privileged: true));
        Assert.Equal("/tmp/iso", ConfigFolder.LocationFor("/tmp/iso", privileged: true));
        var home = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        Assert.Equal(Path.Combine(home, "gergit"), ConfigFolder.LocationFor(null, privileged: false));
        Assert.Equal(Environment.GetEnvironmentVariable("GERGIT_CONFIG_DIR"), ConfigFolder.Location); // tests stay isolated
    }

    [Fact]
    public void A_host_socket_path_stays_within_the_unix_socket_limit()
    {
        Assert.Equal(Path.Combine(_dir, $"gergit-{Environment.MachineName}-42.sock"), Askpass.HostSocketPath(_dir, "-42"));
        var deep = "/" + new string('d', 85 - Environment.MachineName.Length / 2); // too long by about half the host name
        var cut = Askpass.HostSocketPath(deep, "-1234567");
        Assert.Equal(107, Encoding.UTF8.GetByteCount(cut)); // cut exactly to the limit
        Assert.StartsWith(deep + "/gergit-", cut);
        Assert.EndsWith("-1234567.sock", cut);
    }
}
