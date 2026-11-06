using System.Text.RegularExpressions;

namespace GerGit.Core.Git;

public enum CheckState { Running, Passed, Failed, Skipped }

/// <summary>One pre-commit hook of a commit, merge or push. Output = the lines after its status line up to the next hook line.</summary>
public sealed record HookCheck(string Name, CheckState State, string Output);

/// <summary>One item of a hook's progress on the terminal (see <see cref="HookChecks.ParseTty"/>).</summary>
public sealed record ItemCheck(string Name, CheckState State);

/// <summary>
/// The checks of a commit, merge or push: the hooks git runs (the pre-commit framework's "name....Passed" lines on
/// stderr), a hook's per-item progress on the terminal (<see cref="ParseTty"/>) and pytest's summary line in a hook's
/// output. Pure parsing of the text so far: callers re-parse the whole output on each update.
/// </summary>
public static partial class HookChecks
{
    /// <summary>pre-commit's shortest status line: name, dots and status fill its 80 columns but the last.</summary>
    const int MinWidth = 79;

    /// <summary>Longer lines are never a hook line: the regexes below backtrack quadratically on a long run of dots, and
    /// the whole output is re-parsed on each update. pre-commit's lines are about 80 characters.</summary>
    const int MaxLength = 500;

    /// <summary>
    /// Hooks in order. pre-commit writes "name......" before a hook runs and appends its status when the hook ends, all
    /// padded to one width; only lines of that width count, so output such as "foo...Passed" does not split a hook.
    /// <paramref name="pending"/> = the stderr text after the last line break: a "name......" pending line is the running
    /// hook. A complete "name......" line without a status is a hook that never ended (killed; git's next message may
    /// follow on the same line): it stays Running here. ANSI colours are ignored.
    /// </summary>
    public static List<HookCheck> ParseHooks(string output, string pending = "")
    {
        var hooks = new List<HookCheck>();
        var lines = output.Split('\n').Select(l => AnsiRegex().Replace(l, "")).ToArray();
        int width = 0; // of the first status line
        int outputStart = 0;
        string rest = ""; // the text after an unfinished hook's dots
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length > MaxLength) continue;
            if (StatusRegex().Match(line) is { Success: true } m && (width == 0 ? line.Length >= MinWidth : line.Length == width))
            {
                width = line.Length;
                Add(i, m.Groups[1].Value, Enum.Parse<CheckState>(m.Groups[2].Value), "");
            }
            else if (Start(line) is { } s) Add(i, s.Name, CheckState.Running, line[s.Length..]);
        }
        Close(lines.Length);
        if (Start(AnsiRegex().Replace(pending, "")) is { } running) hooks.Add(new(running.Name, CheckState.Running, ""));
        return hooks;

        // "name......" = the status line without its status ("Passed"/"Failed", 6 characters).
        (string Name, int Length)? Start(string line) =>
            line.Length <= MaxLength && StartRegex().Match(line) is { Success: true } r && (width == 0 ? r.Length >= MinWidth - 6 : r.Length == width - 6)
                ? (r.Groups[1].Value, r.Length) : null;

        void Add(int i, string name, CheckState state, string after)
        {
            Close(i);
            hooks.Add(new(name, state, ""));
            outputStart = i + 1;
            rest = after;
        }

        void Close(int end)
        {
            if (hooks.Count == 0) return;
            var text = string.Join('\n', lines[outputStart..end]);
            hooks[^1] = hooks[^1] with { Output = rest.Length == 0 ? text : rest + "\n" + text };
        }
    }

    /// <summary>The last pytest summary line in <paramref name="hookOutput"/> without its "=" padding, such as
    /// "1 failed, 3 passed in 0.12s"; null when it has none. Limitation: only the padded line of pytest 6 and later;
    /// `pytest -q` (no padding) and older versions ("in 0.12 seconds") are not recognised.</summary>
    public static string? ParsePytestSummary(string hookOutput) =>
        PytestSummaryRegex().Matches(AnsiRegex().Replace(hookOutput, "")).LastOrDefault()?.Groups[1].Value;

    /// <summary>
    /// A hook's per-item progress on the terminal (written to /dev/tty, which the pre-commit framework does not
    /// capture), the simple form many test runners and hooks print: a name without spaces when an item starts, "✓" or
    /// "✗" when it ends ("test_a.py ✓ test_b.py ✗ "). Items in order; the last one may still run. A token not yet ended
    /// by whitespace is still being written and left out. Null when the text does not follow that pattern (two names in
    /// a row, a mark without a running item): the caller shows it raw instead of inventing rows.
    /// </summary>
    public static List<ItemCheck>? ParseTty(string text)
    {
        var clean = CleanTty(text);
        var tokens = clean.Split([' ', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
        int count = clean.Length > 0 && !char.IsWhiteSpace(clean[^1]) ? tokens.Length - 1 : tokens.Length;
        var items = new List<ItemCheck>();
        foreach (var t in tokens.Take(count))
        {
            bool running = items.Count > 0 && items[^1].State == CheckState.Running;
            if (t is "✓" or "✗")
            {
                if (!running) return null;
                items[^1] = items[^1] with { State = t == "✓" ? CheckState.Passed : CheckState.Failed };
            }
            else if (running) return null;
            else items.Add(new(t, CheckState.Running));
        }
        return items;
    }

    /// <summary>Terminal text for display: ANSI codes removed; \r\n (\r\r\n after the terminal's own \n→\r\n) and a
    /// lone \r become \n.</summary>
    public static string CleanTty(string text) => CrLfRegex().Replace(AnsiRegex().Replace(text, ""), "\n").Replace('\r', '\n');

    // pre-commit pads the name with dots to its column width; a skipped hook may carry "(no files to check)" before the status.
    [GeneratedRegex(@"^(\S.*?)\.{3,}(?:\([^)]*\))?(Passed|Failed|Skipped)$")]
    private static partial Regex StatusRegex();

    // The dots end at the status (or git's next message, or the end of the text), not at what follows pytest's progress
    // dots: a space ("tests/a.py ........   [ 40%]") or a result letter of a wrapped line ("s......F", "F" not "Failed").
    [GeneratedRegex(@"^([^.\s].*?)\.{3,}(?![.\sEsxX]|F(?!a))")]
    private static partial Regex StartRegex();

    [GeneratedRegex(@"\r+\n")]
    private static partial Regex CrLfRegex();

    // CSI (also private ones such as "\x1b[?25l"), OSC (ended by BEL or ST) and charset selection.
    [GeneratedRegex(@"\x1b(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1b]*(?:\x07|\x1b\\)|[()][0-9A-Za-z])")]
    private static partial Regex AnsiRegex();

    // pytest's last line: "===== 1 failed, 3 passed, 1 warning in 0.12s =====" or "=== no tests ran in 0.01s ===".
    // Not the unpadded line of `pytest -q`, nor "in 0.12 seconds" of pytest before 6.
    [GeneratedRegex(@"^=+ ((?:\d+ \w+|no tests ran)(?:, \d+ \w+)* in \d+(?:\.\d+)?s\b[^=\n]*?) =+\r?$", RegexOptions.Multiline)]
    private static partial Regex PytestSummaryRegex();
}
