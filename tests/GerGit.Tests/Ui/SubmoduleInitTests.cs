using Avalonia.Headless.XUnit;
using GerGit.Core.Git;
using GerGit.ViewModels;

namespace GerGit.Tests.Ui;

/// <summary>"Initialize Submodules..." on the sidebar's Submodules header: the only place besides the clone dialog
/// where GerGit initializes submodules, and only after the user confirmed the URLs. `protocol.file.allow=always` goes
/// through GIT_CONFIG_COUNT (it must reach the clone git starts for the submodule), so the tests run alone.</summary>
[Collection("SubmodulePreference")]
public sealed class SubmoduleInitTests : IDisposable
{
    static readonly string[] ConfigEnv = ["GIT_CONFIG_COUNT", "GIT_CONFIG_KEY_0", "GIT_CONFIG_VALUE_0"];
    readonly string?[] _savedEnv = ConfigEnv.Select(Environment.GetEnvironmentVariable).ToArray();
    readonly TempRepo _sub = new(), _super = new(), _down = new(init: false);
    RepoTabViewModel? _tab;

    public SubmoduleInitTests()
    {
        Environment.SetEnvironmentVariable("GIT_CONFIG_COUNT", "1");
        Environment.SetEnvironmentVariable("GIT_CONFIG_KEY_0", "protocol.file.allow");
        Environment.SetEnvironmentVariable("GIT_CONFIG_VALUE_0", "always");
    }

    public void Dispose()
    {
        _tab?.Dispose();
        for (int i = 0; i < ConfigEnv.Length; i++) Environment.SetEnvironmentVariable(ConfigEnv[i], _savedEnv[i]);
        _sub.Dispose();
        _super.Dispose();
        _down.Dispose();
    }

    [AvaloniaFact]
    public async Task Initialize_submodules_clones_only_after_the_confirmation_that_shows_the_URLs()
    {
        _sub.Commit("s", 0, ("s.txt", "s\n"));
        _super.Git("submodule", "add", "-q", _sub.Path, "sub");
        _super.Commit("p", 0);
        _down.Git("clone", "-q", _super.Path, "."); // the submodule folder stays empty
        _tab = new RepoTabViewModel(await GitRepository.OpenAsync(_down.Path));
        await _tab.RefreshAsync();
        var confirms = new List<(string Title, string Text, string Button)>();
        var errors = new List<string>();
        bool answer = false;
        _tab.Confirm = (title, text, button, _) => { confirms.Add((title, text, button)); return Task.FromResult(answer); };
        _tab.ShowError = (_, text) => { errors.Add(text); return Task.CompletedTask; };
        _tab.ShowGitError = (r, _) => { errors.Add(r.ReadableOutput); return Task.CompletedTask; };
        var marker = Path.Combine(_down.Path, "sub", "s.txt");

        var header = _tab.Sidebar.Roots.Single(n => n.Title == "Submodules");
        var entry = (await _tab.Sidebar.BuildMenuAsync(header))!.Single();
        Assert.Equal("Initialize Submodules...", entry.Header);

        await entry.Invoke(); // declined
        var (title, text, button) = Assert.Single(confirms);
        Assert.Equal(("Initialize Submodules", "Initialize"), (title, button));
        Assert.EndsWith($"\n\nsub: {_sub.Path}", text);
        Assert.False(File.Exists(marker));
        Assert.Empty(_tab.Jobs.History);

        answer = true;
        await _tab.Sidebar.InitializeSubmodulesAsync();
        Assert.Empty(errors);
        Assert.True(File.Exists(marker));
        Assert.Contains("$ git --literal-pathspecs submodule update --init --recursive -- sub\n", _tab.Jobs.History[0].Output);

        await _tab.Sidebar.InitializeSubmodulesAsync(); // nothing left
        Assert.Equal(["There are no submodules to initialize."], errors);
        Assert.Equal(2, confirms.Count);
    }

    [AvaloniaFact]
    public async Task Confirmation_shows_the_URL_git_will_use_escaped_and_with_an_ASCII_host()
    {
        _sub.Commit("s", 0, ("s.txt", "s\n"));
        foreach (var path in new[] { "over", "rel", "sub" }) _super.Git("submodule", "add", "-q", _sub.Path, path);
        const string lookAlike = "g\u0456thub.com"; // Cyrillic "і"
        _super.Git("config", "-f", ".gitmodules", "submodule.sub.url", $"https://{lookAlike}/o/r\u202Egit.exe\tx");
        _super.Git("config", "-f", ".gitmodules", "submodule.rel.url", "../lib.git");
        _super.Commit("p", 0);
        _down.Git("clone", "-q", _super.Path, ".");
        _down.Git("config", "submodule.over.url", "https://example.com/over.git"); // the local URL wins
        _tab = new RepoTabViewModel(await GitRepository.OpenAsync(_down.Path));
        await _tab.RefreshAsync();
        string? text = null;
        _tab.Confirm = (_, t, _, _) => { text = t; return Task.FromResult(false); };

        await _tab.Sidebar.InitializeSubmodulesAsync();

        var host = new System.Globalization.IdnMapping().GetAscii(lookAlike);
        Assert.StartsWith("xn--", host);
        Assert.EndsWith(
            "\n\nover: https://example.com/over.git" +
            $"\nrel: ../lib.git (relative to {_super.Path})" +
            $"\nsub: https://{host}/o/r\\u202Egit.exe\\u0009x", text);
        Assert.DoesNotContain('\u202E', text!);
        Assert.Empty(_tab.Jobs.History);
    }
}
