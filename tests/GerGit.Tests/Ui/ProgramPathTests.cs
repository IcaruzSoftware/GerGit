using System.Diagnostics;
using Avalonia.Headless.XUnit;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;

namespace GerGit.Tests.Ui;

/// <summary>External programs are started by absolute path from rooted PATH entries only. For a bare name .NET looks in
/// the current directory first, so GerGit started inside a cloned repository (`gergit .`) must not run executables named
/// git, setsid, env, sh or xdg-open from it. In the non-parallel "OpenIn" collection: the test changes the process's
/// current directory.</summary>
[Collection("OpenIn")]
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ProgramPathTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("gergit-programpath-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    string Stub(string dir, string name, string body)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    [Fact]
    public void Only_rooted_PATH_entries_are_searched()
    {
        var abs = Directory.CreateDirectory(Path.Combine(_dir, "abs")).FullName;
        Directory.CreateDirectory(Path.Combine(_dir, "rel"));
        var inRel = Stub(Path.Combine(_dir, "rel"), "tool", "");
        var inAbs = Stub(abs, "tool", "");
        Stub(_dir, "tool", "");
        var old = Environment.CurrentDirectory;
        Environment.CurrentDirectory = _dir;
        try
        {
            Assert.Null(ProgramPath.Find("tool", "rel"));
            Assert.Null(ProgramPath.Find("tool", ".::"));
            Assert.Equal(inAbs, ProgramPath.Find("tool", $"rel:.::{abs}"));
            Assert.Equal(inRel, ProgramPath.Find("rel/tool", "")); // a name with a slash is a path
            Assert.Null(ProgramPath.Find("rel", abs + ":" + _dir)); // a folder is not a program
        }
        finally { Environment.CurrentDirectory = old; }
    }

    [Fact]
    public async Task Git_check_names_a_missing_setsid_instead_of_a_missing_git()
    {
        Assert.Equal(new OpenFailure("Git Error", "Cannot find 'setsid' on PATH"),
            MainWindowViewModel.GitFailure(null, "Cannot find 'setsid' on PATH"));
        var notFound = new OpenFailure("Git Error", $"Cannot find git instance at: '{GitRunner.GitPath}'");
        Assert.Equal(notFound, MainWindowViewModel.GitFailure(null, "Cannot find git instance at: 'git'"));
        Assert.Equal(notFound, MainWindowViewModel.GitFailure(null, ""));
        Assert.Equal("Git 2.20.0 is too old. GerGit needs git 2.38 or newer.", MainWindowViewModel.GitFailure(new(2, 20, 0), "")!.Text);

        // The real probe: a version and no error, and nothing to show.
        var (version, error) = await GitRunner.ProbeVersionAsync();
        Assert.Equal("", error);
        Assert.Null(MainWindowViewModel.GitFailure(version, error));

        // A start failure's text is the result's stderr, which the probe passes on (a non-absolute git never starts).
        var start = await GitRunner.RunProgramAsync("git-not-there", null, ["version"], null, false, null, default);
        Assert.Equal("Cannot find git instance at: 'git-not-there'", start.Stderr);
    }

    [AvaloniaFact]
    public async Task Executables_in_the_current_directory_never_run()
    {
        using var repo = new TempRepo();
        var head = repo.Commit("first", 1, ("a.txt", "a\n")); // before the cd: TempRepo itself starts the bare name "git"
        var markers = Directory.CreateDirectory(Path.Combine(_dir, "markers")).FullName;
        var cwd = Directory.CreateDirectory(Path.Combine(_dir, "cloned-repo")).FullName;
        foreach (var name in (string[])["git", "setsid", "env", "sh", "xdg-open", "git-not-there"])
            Stub(cwd, name, $"touch '{markers}/{name}'\n");
        var bin = Directory.CreateDirectory(Path.Combine(_dir, "bin")).FullName;
        var terminal = Stub(bin, "kitty", "pwd > \"$0.out.tmp\"; mv \"$0.out.tmp\" \"$0.out\"\n");

        var oldCwd = Environment.CurrentDirectory;
        Environment.CurrentDirectory = cwd;
        OpenIn.Env = name => name == "PATH" ? bin : null;
        var savedTerminal = AppPreferences.Current.Terminal;
        AppPreferences.Current.Terminal = new TerminalSetting();
        RepoTabViewModel? tab = null;
        try
        {
            Assert.True(Path.IsPathRooted(GitRunner.GitPath));
            Assert.True(GitRunner.HasSetsid && Path.IsPathRooted(GitRunner.SetsidPath!));

            // git, plain and with the pseudo-terminal start (/bin/sh + setsid --ctty).
            Assert.Equal(head, (await GitRunner.RunAsync(repo.Path, ["rev-parse", "HEAD"])).Stdout.Trim());
            var tty = await GitRunner.RunProgramAsync(GitRunner.GitPath, repo.Path, ["version"], null, false, new Job("x"), default, tty: true);
            Assert.True(tty.Success, tty.Stderr);
            // A git that cannot be resolved is not looked up anywhere: the "git not found" text.
            var missing = await GitRunner.RunProgramAsync("git-not-there", repo.Path, ["version"], null, false, null, default);
            Assert.Equal("Cannot find git instance at: 'git-not-there'", missing.Stderr);

            // The terminal (through setsid) and xdg-open through their entry points.
            tab = new RepoTabViewModel(await GitRepository.OpenAsync(repo.Path));
            var errors = new List<string>();
            tab.ShowError = (_, text) => { errors.Add(text); return Task.CompletedTask; };
            await OpenIn.TerminalAsync(tab);
            var sw = Stopwatch.StartNew();
            while (!File.Exists(terminal + ".out") && sw.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(20);
            Assert.Equal(tab.Path, File.ReadAllText(terminal + ".out").Trim());
            Assert.Empty(errors);
            // xdg-open (HostingService.OpenInBrowser) is started by this path, never by the stub's name; not run here, so
            // no real file manager or browser opens.
            Assert.True(ProgramPath.Of("xdg-open") is null or ['/', ..]);
            Assert.NotEqual(Path.Combine(cwd, "xdg-open"), ProgramPath.Of("xdg-open"));

            await Task.Delay(500); // started programs had time to run a stub
            Assert.Empty(Directory.GetFiles(markers));
        }
        finally
        {
            Environment.CurrentDirectory = oldCwd;
            OpenIn.Env = Environment.GetEnvironmentVariable;
            AppPreferences.Current.Terminal = savedTerminal;
            tab?.Dispose();
        }
    }
}
