using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace GerGit.Core.Git;

/// <summary>
/// The editor helper of the interactive rebase. git runs the helper script (written
/// by <see cref="Askpass.Start"/>) as sequence and core editor, which starts GerGit with `--rebase-editor &lt;file&gt;`.
/// Todo requests go over the askpass socket (kind <see cref="TodoRequest"/>, payload = todo path) to the session that
/// waits for this repository; commit messages come from the message archive and need no app.
/// GerGit uses the existing GERGIT_PID  and the askpass socket.
/// </summary>
public static class RebaseEditor
{
    internal const string TodoRequest = "rebase-todo";

    /// <summary>The message archive next to the todo: `sha \0 message \0` per commit (GerGit's own format).</summary>
    internal const string ArchiveName = "gergit-messages";

    static readonly ConcurrentDictionary<string, Func<string, Task<string>>> Waiting = new(StringComparer.Ordinal);

    /// <summary>`-c sequence.editor=&lt;helper&gt; -c core.editor=&lt;helper&gt;`; empty while the helper server does not run.</summary>
    public static IReadOnlyList<string> ConfigArgs() =>
        Askpass.EditorPath is { } path
            ? ["-c", "sequence.editor=" + Askpass.ShQuote(path), "-c", "core.editor=" + Askpass.ShQuote(path)]
            : [];

    /// <summary>The handler that answers the todo request of git running in <paramref name="workTree"/>
    /// (= GERGIT_REPO_PATH in the helper). False when another one is registered.</summary>
    internal static bool Register(string workTree, Func<string, Task<string>> onTodo) => Waiting.TryAdd(workTree, onTodo);

    internal static void Unregister(string workTree, Func<string, Task<string>> onTodo) =>
        Waiting.TryRemove(new KeyValuePair<string, Func<string, Task<string>>>(workTree, onTodo));

    /// <summary>App side: `start` or `cancel`. Nobody waiting for this repository = cancel.</summary>
    internal static Task<string> AnswerTodoAsync(string repoPath, string todoPath) =>
        Waiting.TryGetValue(repoPath, out var onTodo) ? onTodo(todoPath) : Task.FromResult("cancel");

    internal static void WriteArchive(string rebaseDir, IReadOnlyDictionary<string, string> messages) =>
        File.WriteAllText(Path.Combine(rebaseDir, ArchiveName), string.Concat(messages.Select(kv => kv.Key + "\0" + kv.Value + "\0")));

    // ---- Helper side (a separate GerGit process started by git) ----

    /// <summary>`GerGit --rebase-editor &lt;file&gt;`: the todo goes to the app, COMMIT_EDITMSG gets the archived
    /// message, any other file is left alone. Exit 0 except when the app cannot be reached for a todo.</summary>
    public static int RunHelper(string[] args)
    {
        if (args is not [var file]) return 0;
        var path = Path.GetFullPath(file);
        if (path.EndsWith("git-rebase-todo", StringComparison.Ordinal)) return EditTodo(path);
        if (path.EndsWith("COMMIT_EDITMSG", StringComparison.Ordinal)) ApplyArchivedMessage(path);
        return 0;
    }

    /// <summary>Asks the app and waits as long as it takes (the app answers within its timeout). Anything but `start`
    /// empties the todo, so git ends with "nothing to do": git could run its unedited todo on an unknown answer,
    /// GerGit never starts a rebase the user did not confirm (also used for the timeout and a lost app).</summary>
    static int EditTodo(string path)
    {
        if (OperatingSystem.IsWindows()) return 1;
        var pidText = Environment.GetEnvironmentVariable("GERGIT_PID");
        using var socket = int.TryParse(pidText, out var pid) ? Askpass.Connect(pid, TimeSpan.FromSeconds(10)) : null;
        if (socket is null)
        {
            Console.Error.WriteLine($"GerGit process not found (pid '{pidText}')");
            return 1;
        }
        var repoPath = Environment.GetEnvironmentVariable("GERGIT_REPO_PATH") ?? "";
        using var stream = new NetworkStream(socket);
        stream.Write(Encoding.UTF8.GetBytes(string.Join('\0', TodoRequest, "0", repoPath, path)));
        socket.Shutdown(SocketShutdown.Send);
        var answer = new MemoryStream();
        try { stream.CopyTo(answer); }
        catch (IOException) { } // the app went away: cancel
        if (Encoding.UTF8.GetString(answer.ToArray()) != "start") File.WriteAllText(path, "");
        return 0;
    }

    /// <summary>
    /// COMMIT_EDITMSG at a reword/squash step: the step is the last commit line of `rebase-merge/done` (a `noop` line in
    /// the todo or done ends it); its SHA is looked up in the archive. Not found or unreadable: git's text stays.
    /// </summary>
    internal static void ApplyArchivedMessage(string messageFile)
    {
        try
        {
            var dir = Path.Combine(Path.GetDirectoryName(messageFile)!, "rebase-merge");
            var sha = StepSha(ReadOrNull(Path.Combine(dir, "git-rebase-todo")), ReadOrNull(Path.Combine(dir, "done")));
            if (sha is null || ReadOrNull(Path.Combine(dir, ArchiveName)) is not { } archive) return;
            var f = archive.Split('\0');
            for (int i = 0; i + 1 < f.Length; i += 2)
                if (f[i].Length > 0 && (f[i].StartsWith(sha, StringComparison.Ordinal) || sha.StartsWith(f[i], StringComparison.Ordinal)))
                {
                    File.WriteAllText(messageFile, f[i + 1] + "\n");
                    return;
                }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static string? ReadOrNull(string path) => File.Exists(path) ? File.ReadAllText(path) : null;

    /// <summary>SHA of the last `&lt;action&gt; &lt;sha&gt; &lt;rest&gt;` line of <paramref name="done"/> whose action
    /// starts with p r e s f d u; null when there is none or either file has a `noop` line.</summary>
    internal static string? StepSha(string? todo, string? done)
    {
        string? sha = null;
        foreach (var (text, isDone) in new[] { (todo, false), (done, true) })
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.Trim().Length == 0 || line[0] == '#') continue;
                if (line == "noop") return null;
                var parts = line.Split(' ', 3);
                if (isDone && parts.Length == 3 && parts[2].Length > 0 && parts[1].Length > 0 && "prsefdu".Contains(line[0]))
                    sha = parts[1];
            }
        return sha;
    }
}
