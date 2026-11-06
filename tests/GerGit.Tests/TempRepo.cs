using System.Diagnostics;

namespace GerGit.Tests;

/// <summary>A throwaway git repository under the temp folder; deleted on Dispose.</summary>
public sealed class TempRepo : IDisposable
{
    public string Path { get; }

    public TempRepo(bool init = true)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gergit-test-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
        if (!init) return;
        Git("init", "-q", "-b", "main");
        Git("config", "user.name", "Test User");
        Git("config", "user.email", "test@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
    }

    public void Write(string relPath, string content)
    {
        var full = System.IO.Path.Combine(Path, relPath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    /// <summary>Writes the files, stages everything and commits; returns the new commit SHA.
    /// Date = 2020-01-01 + <paramref name="seconds"/> so walk order is deterministic.</summary>
    public string Commit(string message, int seconds, params (string Path, string Content)[] files)
    {
        foreach (var (p, c) in files) Write(p, c);
        Git("add", "-A");
        var date = $"@{1577836800 + seconds} +0000";
        GitEnv(new() { ["GIT_AUTHOR_DATE"] = date, ["GIT_COMMITTER_DATE"] = date }, "commit", "-q", "--allow-empty", "-m", message);
        return Git("rev-parse", "HEAD");
    }

    /// <summary>Runs git in the repository; returns trimmed stdout, throws on failure.</summary>
    public string Git(params string[] args) => GitEnv([], args);

    public string GitEnv(Dictionary<string, string> env, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = Path, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        psi.Environment["GIT_CONFIG_GLOBAL"] = "/dev/null"; // keep the user's global config out of the tests
        psi.Environment["LC_ALL"] = "C";
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Result.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}
