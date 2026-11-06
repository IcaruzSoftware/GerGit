using System.Diagnostics;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>Process groups, the cancel ladder and job runs.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Collection("RefOps")] // GitRunner.PtyDisabled is static; RefDialogsTests set it
public sealed class JobRunTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gergit-jobrun-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Script(string name, string body)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        return path;
    }

    static bool Alive(int pid)
    {
        try
        {
            var stat = File.ReadAllText($"/proc/{pid}/stat");
            return stat[stat.LastIndexOf(')') + 2] != 'Z'; // a zombie is dead, only not reaped yet
        }
        catch (IOException) { return false; }
    }

    static async Task WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException();
            await Task.Delay(20);
        }
    }

    [Fact]
    public async Task Process_RunsAsItsOwnGroupAndSessionLeader_WithTheStartedPid()
    {
        var fake = Script("ids", "read -r pid comm state ppid pgrp sid rest < /proc/$$/stat\necho \"$pid $ppid $pgrp $sid\"\n");
        var r = await GitRunner.RunProgramAsync(fake, _dir, [], null, false, null, default);
        var ids = r.Stdout.Split(' ').Select(int.Parse).ToArray();
        Assert.Equal(Environment.ProcessId, ids[1]); // setsid exec'd in place: our direct child is the program itself
        Assert.Equal(ids[0], ids[2]);                // process group = its pid
        Assert.Equal(ids[0], ids[3]);                // own session, no controlling terminal
    }

    [Fact]
    public async Task Cancel_SendsSigintThenSigtermAfterTwoSeconds_AndKillsTheGroup()
    {
        var log = Path.Combine(_dir, "log");
        // Ignores SIGINT; its child (background job: SIGINT ignored by the shell) keeps the stdout pipe open.
        var fake = Script("trap", $$"""
            trap 'echo "INT $(date +%s%N)" >> "{{log}}"' INT
            trap 'echo "TERM $(date +%s%N)" >> "{{log}}"; exit 143' TERM
            sleep 300 &
            echo "$!" > "{{log}}.child"
            while :; do wait; done
            """);
        using var cts = new CancellationTokenSource();
        var run = GitRunner.RunProgramAsync(fake, _dir, [], null, false, null, cts.Token);
        await WaitFor(() => File.Exists(log + ".child") && File.ReadAllText(log + ".child").EndsWith('\n'));
        cts.Cancel();
        var r = await run;

        Assert.True(r.Cancelled);
        var lines = File.ReadAllLines(log).Select(l => l.Split(' ')).ToArray();
        Assert.Equal(["INT", "TERM"], lines.Select(l => l[0]));
        var gap = TimeSpan.FromTicks((long.Parse(lines[1][1]) - long.Parse(lines[0][1])) / 100);
        Assert.InRange(gap, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10)); // trap timestamps come from a forked `date`
        // The pipe closes just before the kernel marks the child dead.
        await WaitFor(() => !Alive(int.Parse(File.ReadAllText(log + ".child"))));
    }

    [Fact]
    public async Task Cancel_ProgramExitsOnSigint_LeftoverChildIsKilledWithoutSigterm()
    {
        if (Environment.GetEnvironmentVariable(SigintIgnoredFlag) == "1")
            Assert.True(SigintIgnored(), "the inner run must start with SIGINT ignored");
        var log = Path.Combine(_dir, "log");
        var fake = Script("int", $$"""
            trap 'echo INT >> "{{log}}"; exit 130' INT
            trap 'echo TERM >> "{{log}}"; exit 143' TERM
            sleep 300 > /dev/null 2>&1 &
            echo "$!" > "{{log}}.child"
            while :; do wait; done
            """);
        using var cts = new CancellationTokenSource();
        var run = GitRunner.RunProgramAsync(fake, _dir, [], null, false, null, cts.Token);
        await WaitFor(() => File.Exists(log + ".child") && File.ReadAllText(log + ".child").EndsWith('\n'));
        cts.Cancel();
        var r = await run;

        Assert.True(r.Cancelled);
        Assert.Equal(["INT"], File.ReadAllLines(log));
        var child = int.Parse(File.ReadAllText(log + ".child"));
        await WaitFor(() => !Alive(child)); // SIGKILL is asynchronous and the child holds no pipe of ours
    }

    /// <summary>An app started with SIGINT ignored (`&` in a script, nohup) passes SIG_IGN on to its children unless the
    /// runner resets it. Reruns the test above in a test host started exactly like that.</summary>
    [Fact]
    public async Task Cancel_WorksWhenTheAppStartedWithSigintIgnored()
    {
        var psi = new ProcessStartInfo("sh") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "trap '' INT; exec \"$0\" -method \"$1\"", Path.Combine(AppContext.BaseDirectory, "GerGit.Tests"),
                     $"{typeof(JobRunTests).FullName}.{nameof(Cancel_ProgramExitsOnSigint_LeftoverChildIsKilledWithoutSigterm)}"])
            psi.ArgumentList.Add(a);
        psi.Environment[SigintIgnoredFlag] = "1";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var output = await stdout + await stderr;
        Assert.True(p.ExitCode == 0, output);
        Assert.Contains("Total: 1, Errors: 0, Failed: 0", output);
    }

    const string SigintIgnoredFlag = "GERGIT_TEST_SIGINT_IGNORED";

    static bool SigintIgnored() =>
        (Convert.ToUInt64(File.ReadLines("/proc/self/status").First(l => l.StartsWith("SigIgn:"))[7..].Trim(), 16) & 2) != 0;

    [Fact]
    public async Task ReadLines_EarlyBreak_KillsTheWholeGroup()
    {
        using var repo = new TempRepo();
        string? first = null;
        await foreach (var line in GitRunner.ReadLinesAsync(repo.Path, ["-c", "alias.kids=!sleep 300 & echo $!; yes", "kids"]))
        {
            first = line;
            break;
        }
        await WaitFor(() => !Alive(int.Parse(first!)));
    }

    [Fact]
    public async Task MissingExecutable_IsCannotFindGit()
    {
        var r = await GitRunner.RunProgramAsync(Path.Combine(_dir, "no-git"), _dir, ["version"], null, false, null, default);
        Assert.Equal(-1, r.ExitCode);
        Assert.Equal($"Cannot find git instance at: '{Path.Combine(_dir, "no-git")}'", r.Stderr);
    }

    [Fact]
    public async Task Job_HeaderAndOutput()
    {
        using var repo = new TempRepo();
        var job = new Job("test");
        job.AppendOutput("gergit: note");
        var r = await GitRunner.RunJobAsync(job, repo.Path, ["-c", "push.default=upstream", "hash-object", "--stdin", "--progress"], "hello\n");
        // --progress is unknown to hash-object; the header is still written, with the hidden parts removed.
        Assert.False(r.Success);
        Assert.StartsWith("gergit: note\n\n$ git hash-object --stdin\n\n", job.Output);

        job = new Job("test");
        r = await GitRunner.RunJobAsync(job, repo.Path, ["hash-object", "--stdin"], "hello\n");
        var once = "$ git hash-object --stdin\n\nce013625030ba8dba906f756967f9e9ca394464a\n";
        Assert.Equal(once, job.Output);
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a\n", r.Stdout);
        await GitRunner.RunJobAsync(job, repo.Path, ["hash-object", "--stdin"], "hello\n");
        Assert.Equal(once + "\n" + once, job.Output); // exactly one blank line between commands

        job = new Job("test");
        await GitRunner.RunJobAsync(job, repo.Path, ["-c", "alias.x=!true", "x", "a b"]);
        Assert.Equal("$ git -c alias.x=!true x \"a b\"\n\n", job.Output);
    }

    [Fact]
    public async Task Job_ProgressLinesReportAndStayOutOfOutput()
    {
        using var repo = new TempRepo();
        var job = new Job("test");
        var r = await GitRunner.RunJobAsync(job, repo.Path, ["-c",
            @"alias.prog=!printf 'out1\nout2\n'; printf 'Receiving objects:  45%% (1/2)\rReceiving objects: 100%% (2/2), done.\nplain err\nremote: \033[KCounting objects: 20%% (1/5)\r\n' >&2",
            "prog"]);
        Assert.True(r.Success);
        Assert.Equal("out1\nout2\n", r.Stdout);
        Assert.Equal("plain err\n", r.Stderr);
        Assert.Contains("\nout1\n", job.Output); // stdout and stderr lines may interleave
        Assert.Contains("\nout2\n", job.Output);
        Assert.Contains("\nplain err\n", job.Output);
        Assert.DoesNotContain("objects", job.Output[job.Output.IndexOf("\n\n")..]); // the header shows the alias itself
        Assert.Equal(20, job.Progress);
        Assert.Equal("Counting objects: 20%", job.Message);
        Assert.Equal(JobOutcome.Running, job.Outcome); // the runner never finishes the job
    }

    [Fact]
    public async Task Job_ProgressShowsAtTheCarriageReturn_NotWithTheNextUpdate()
    {
        using var repo = new TempRepo();
        var job = new Job("test");
        var run = GitRunner.RunJobAsync(job, repo.Path, ["-c", @"alias.stall=!printf 'Receiving objects:  45%% (9/20)\r' >&2; sleep 30", "stall"]);
        await WaitFor(() => job.Progress == 45);
        job.Cancel();
        Assert.True((await run).Cancelled);
    }

    [Fact]
    public async Task Job_PendingLine_ShowsTextWithoutALineBreak_AndTheEnvReachesTheProgram()
    {
        // pre-commit writes "name....." before a hook runs and its status when the hook ends.
        var fake = Script("hook", "printf 'pytest.....' >&2\nsleep 0.5\nprintf 'Passed\\n' >&2\necho \"$EXTRA\"\n");
        var job = new Job("x");
        var run = GitRunner.RunProgramAsync(fake, _dir, [], null, false, job, default, header: false,
            env: new Dictionary<string, string> { ["EXTRA"] = "from env" });
        await WaitFor(() => job.PendingLine == "pytest.....");
        Assert.Equal("", job.Output);
        await run;
        Assert.Equal("", job.PendingLine);
        Assert.Contains("pytest.....Passed\n", job.Output);
        Assert.Contains("from env\n", job.Output);
    }

    [Fact]
    public async Task Job_Tty_IsTheControllingTerminal_AndItsTextGoesToTheJob()
    {
        var fake = Script("tty", """
            read -r pid comm state ppid pgrp sid rest < /proc/$$/stat
            echo "ids $pid $ppid $pgrp $sid"
            [ -t 0 ] && echo stdin-is-a-tty
            [ -t 1 ] || echo stdout-is-a-pipe
            printf 'test_a.py ' > /dev/tty && sleep 0.2 && printf '\342\234\223 ' > /dev/tty
            """);
        var job = new Job("x");
        var r = await GitRunner.RunProgramAsync(fake, _dir, [], null, false, job, default, header: false, tty: true);
        Assert.True(r.Success, r.Stderr);
        var ids = job.Output.Split('\n')[0].Split(' ')[1..].Select(int.Parse).ToArray();
        Assert.Equal(Environment.ProcessId, ids[1]); // sh and setsid exec'd in place: our child is the program
        Assert.Equal((ids[0], ids[0]), (ids[2], ids[3])); // own group and session, as without a tty
        Assert.Contains("stdin-is-a-tty\nstdout-is-a-pipe\n", job.Output);
        Assert.Equal("test_a.py ✓ ", job.TtyText);
    }

    [Fact]
    public async Task Job_Tty_StartWithoutSetsid_RunsOnceMoreWithoutTheTerminal()
    {
        // setsid is started by its absolute path; a setsid that fails the --ctty start the way sh reports a program it
        // cannot find ("sh: … not found", 127) stands for one that went away. Without the pty the real one is used.
        var real = GitRunner.SetsidPath!;
        var fakeSetsid = Script("fake-setsid",
            $"if [ \"$1\" = -c ]; then echo 'sh: 1: exec: setsid: not found' >&2; exit 127; fi\nexec '{real}' \"$@\"\n");
        var fake = Script("probe", "if [ -t 0 ]; then echo with-tty; else echo without-tty; fi\n");
        var job = new Job("x");
        GitRunner.SetsidPath = fakeSetsid;
        try
        {
            var r = await GitRunner.RunProgramAsync(fake, _dir, [], null, false, job, default, header: false, tty: true);
            Assert.True(r.Success, r.Stderr);
            Assert.Equal("without-tty\n", r.Stdout);
        }
        finally { GitRunner.SetsidPath = real; }
        Assert.Contains("not found", job.Output); // the failed start stays visible
    }

    /// <summary>`git commit` (dies at a SIGINT) runs a pre-commit hook that stands for pre-commit restoring its stash on
    /// SIGINT (a 1 s cleanup).</summary>
    [Theory]
    [InlineData(0, false, false)] // the plain ladder: git dies at the SIGINT, the rest of the group is killed at once
    [InlineData(10, false, true)] // hooks: only the hook is interrupted; git ends by itself after it
    [InlineData(10, true, true)]  // the same with the pty, where git's end as session leader would SIGHUP the hook
    public async Task Cancel_HooksWait_LetsTheHooksSigintTrapFinish(int hooksWait, bool tty, bool finished)
    {
        using var repo = new TempRepo();
        repo.Commit("init", 0, ("a.txt", "a\n"));
        repo.Write("a.txt", "a2\n");
        repo.Git("add", "a.txt");
        var marker = Path.Combine(_dir, "restored");
        var hooks = Directory.CreateDirectory(Path.Combine(repo.Path, ".git", "hooks")).FullName;
        File.WriteAllText(Path.Combine(hooks, "pre-commit"),
            $"#!/bin/sh\ntrap 'sleep 1; echo done > \"{marker}\"; exit 130' INT\necho ready\nsleep 30\n");
        File.SetUnixFileMode(Path.Combine(hooks, "pre-commit"), (UnixFileMode)0b111_101_101);
        repo.Git("config", "core.hooksPath", hooks);
        var job = new Job("x");
        using var cts = new CancellationTokenSource();
        var run = GitRunner.RunProgramAsync(GitRunner.GitPath, repo.Path, ["commit", "-q", "-m", "x"], null, false, job,
            cts.Token, header: false, tty: tty, hooksWait: hooksWait > 0 ? TimeSpan.FromSeconds(hooksWait) : null);
        await WaitFor(() => job.Output.Contains("ready"));
        cts.Cancel();
        var r = await run;
        Assert.True(r.Cancelled);
        await Task.Delay(1500); // a hook that outlived the cancel would write the marker now
        Assert.Equal(finished, File.Exists(marker));
        Assert.Equal("init", repo.Git("log", "-1", "--format=%s")); // never committed
        if (finished) Assert.Equal(1, r.ExitCode); // git was not interrupted: it ended because its hook failed
    }

    [Fact]
    public async Task Job_WithoutHeader_OnlyGitOutput()
    {
        using var repo = new TempRepo();
        var job = new Job("test");
        await GitRunner.RunJobAsync(job, repo.Path, ["hash-object", "--stdin"], "hello\n", header: false);
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a\n", job.Output);
    }

    [Theory]
    [InlineData("Receiving objects:  45% (9/20)", 45, "Receiving objects: 45%")]
    [InlineData("remote: Counting objects: 100% (5/5), done.", 100, "Counting objects: 100%")]
    [InlineData("remote: \u001b[KCompressing objects:  60% (3/5)", 60, "Compressing objects: 60%")]
    [InlineData("Updating files: 12% (1/8)", 12, "Updating files: 12%")]
    public void ParseProgress(string line, int percent, string message) =>
        Assert.Equal((percent, message), GitRunner.ParseProgress(line));

    [Theory]
    [InlineData("From /tmp/x")]
    [InlineData(" * [new branch]      main       -> origin/main")]
    public void ParseProgress_NotProgress(string line) => Assert.Null(GitRunner.ParseProgress(line));

    [Fact]
    public async Task Job_Cancel_UsesTheLadderAndReportsCancelled()
    {
        using var repo = new TempRepo();
        var job = new Job("test");
        var run = GitRunner.RunJobAsync(job, repo.Path, ["-c", "alias.slow=!echo started; sleep 30", "slow"]);
        await WaitFor(() => job.Output.Contains("started"));
        job.Cancel();
        var r = await run;
        Assert.True(r.Cancelled);
        Assert.Equal("canceled", job.Message);
    }

    [Fact]
    public async Task Job_AlreadyCancelled_RunsNothing_UnlessIgnoreCancel()
    {
        using var repo = new TempRepo();
        var job = new Job("test");
        job.Cancel();
        var r = await GitRunner.RunJobAsync(job, repo.Path, ["hash-object", "--stdin"], "hello\n");
        Assert.True(r.Cancelled);
        Assert.Equal("", job.Output);

        r = await GitRunner.RunJobAsync(job, repo.Path, ["hash-object", "--stdin"], "hello\n", ignoreCancel: true);
        Assert.True(r.Success);
        Assert.Equal("ce013625030ba8dba906f756967f9e9ca394464a\n", r.Stdout);
    }

    [Fact]
    public async Task Environment_PromptVariables()
    {
        using var repo = new TempRepo();
        string[] args = ["-c", "alias.envtest=!echo \"$GERGIT_NO_PROMPT|$GERGIT_PID|$GERGIT_REPO_PATH\"", "envtest"];
        var r = await GitRunner.RunJobAsync(new Job("test"), repo.Path, args, silent: true);
        Assert.Equal($"1|{Environment.ProcessId}|{repo.Path}", r.Stdout.Trim());
        r = await GitRunner.RunAsync(repo.Path, args);
        Assert.Equal($"0|{Environment.ProcessId}|{repo.Path}", r.Stdout.Trim());
    }
}
