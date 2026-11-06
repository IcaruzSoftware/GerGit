using GerGit.Core.Git;

namespace GerGit.Tests.Core;

/// <summary>pre-commit status lines, pytest's summary line and a hook's item progress on the terminal.</summary>
public class HookChecksTests
{
    const string Green = """
        $ git merge b --no-ff

        ruff format..............................................................Passed
        pytest...................................................................Passed
        - hook id: pytest
        - duration: 12.3s

        ============================== 14 passed in 2.31s ==============================
        Merge made by the 'ort' strategy.
         a.txt | 2 +-
         1 file changed, 1 insertion(+), 1 deletion(-)

        """;

    const string Red = """
        $ git merge b --no-ff

        ruff format..............................................................Passed
        isort................................................(no files to check)Skipped
        pytest...................................................................Failed
        - hook id: pytest
        - exit code: 1

        ============================= test session starts ==============================
        tests/test_a.py ..F                                                      [100%]
        =========================== short test summary info ============================
        FAILED tests/test_a.py::test_x
        ===================== 1 failed, 2 passed, 1 warning in 0.12s ===================

        Not committing merge; use 'git commit' to complete the merge.

        """;

    [Fact]
    public void All_green()
    {
        var hooks = HookChecks.ParseHooks(Green);
        Assert.Equal([("ruff format", CheckState.Passed), ("pytest", CheckState.Passed)], hooks.Select(h => (h.Name, h.State)));
        Assert.Equal("14 passed in 2.31s", HookChecks.ParsePytestSummary(hooks[1].Output));
        Assert.Null(HookChecks.ParsePytestSummary(hooks[0].Output));
    }

    [Fact]
    public void Failed_hook_with_a_pytest_summary()
    {
        var hooks = HookChecks.ParseHooks(Red);
        Assert.Equal([("ruff format", CheckState.Passed), ("isort", CheckState.Skipped), ("pytest", CheckState.Failed)],
            hooks.Select(h => (h.Name, h.State)));
        Assert.StartsWith("- hook id: pytest\n- exit code: 1\n", hooks[2].Output);
        Assert.Equal("1 failed, 2 passed, 1 warning in 0.12s", HookChecks.ParsePytestSummary(hooks[2].Output));
    }

    [Fact]
    public void Pytest_summary_lines()
    {
        Assert.Equal("1 error, 1 xfailed, 2 warnings in 0.30s", HookChecks.ParsePytestSummary("=== 1 error, 1 xfailed, 2 warnings in 0.30s ===\n"));
        Assert.Equal("no tests ran in 0.01s", HookChecks.ParsePytestSummary("=== no tests ran in 0.01s ===\n"));
        Assert.Equal("5 passed in 75.20s (0:01:15)", HookChecks.ParsePytestSummary("== 5 passed in 75.20s (0:01:15) ==\r\n"));
        Assert.Equal("2 passed in 0.02s", HookChecks.ParsePytestSummary("\x1b[32m=== \x1b[1m2 passed\x1b[0m\x1b[32m in 0.02s ===\x1b[0m"));
        // The last one counts; the other "=" lines of pytest's output are no summary.
        Assert.Equal("1 passed in 1s", HookChecks.ParsePytestSummary("=== 1 failed in 1s ===\n=== test session starts ===\n=== 1 passed in 1s ===\n"));
        Assert.Null(HookChecks.ParsePytestSummary("=== test session starts ===\n=== short test summary info ===\n1 passed in 0.1s\n"));
    }

    /// <summary>A pre-commit line: name, dots, optional note and status in 79 columns (80 columns minus one).</summary>
    internal static string Line(string name, string status, string note = "") =>
        name + new string('.', 79 - name.Length - note.Length - status.Length) + note + status;

    /// <summary>The line before the hook ends: its status (6 characters) is still missing.</summary>
    internal static string Dots(string name) => name + new string('.', 73 - name.Length);

    [Fact]
    public void Skipped_hooks_and_the_running_hook_from_the_pending_line()
    {
        var text = Line("black", "Skipped", "(no files to check)") + "\n" + Line("isort", "Skipped") + "\n";
        Assert.Equal([("black", CheckState.Skipped), ("isort", CheckState.Skipped)],
            HookChecks.ParseHooks(text).Select(h => (h.Name, h.State)));
        var running = HookChecks.ParseHooks(text, Dots("pytest"));
        Assert.Equal(("pytest", CheckState.Running), (running[2].Name, running[2].State));
        // A pending line that is not pre-commit's dots line adds nothing.
        Assert.Equal(2, HookChecks.ParseHooks(text, "Updating 1a2b..3c4d").Count);
        Assert.Equal(2, HookChecks.ParseHooks(text, "pytest.....").Count);
    }

    [Fact]
    public void Only_padded_status_lines_count_and_colours_are_ignored()
    {
        // pre-commit with colour: the status is wrapped in escape sequences.
        var text = Line("ruff", "Passed").Replace("Passed", "\x1b[42mPassed\x1b[m") + "\n" +
                   Line("pytest", "Failed") + "\n- hook id: pytest\n\nfoo...Passed\nbar........Failed\n=== 1 failed in 0.10s ===\n";
        var hooks = HookChecks.ParseHooks(text);
        Assert.Equal([("ruff", CheckState.Passed), ("pytest", CheckState.Failed)], hooks.Select(h => (h.Name, h.State)));
        Assert.Equal("1 failed in 0.10s", HookChecks.ParsePytestSummary(hooks[1].Output));
        Assert.Equal("ruff", HookChecks.ParseHooks("\x1b[1m" + Line("ruff", "Passed") + "\x1b[0m\n").Single().Name);
    }

    [Fact]
    public void A_hook_without_a_status_line_at_the_end_stays_running()
    {
        // The hook was killed: pre-commit never appended the status, the runner emitted the dots line at the end.
        var hooks = HookChecks.ParseHooks(Line("ruff", "Passed") + "\n" + Dots("pytest") + "\nNot committing merge\n");
        Assert.Equal([("ruff", CheckState.Passed), ("pytest", CheckState.Running)], hooks.Select(h => (h.Name, h.State)));
        // git's message right after the dots, on the same line.
        hooks = HookChecks.ParseHooks(Line("ruff", "Passed") + "\n" + Dots("pytest") + "Not committing merge\n");
        Assert.Equal([("ruff", CheckState.Passed), ("pytest", CheckState.Running)], hooks.Select(h => (h.Name, h.State)));
        Assert.StartsWith("Not committing merge\n", hooks[1].Output);
        // A partly written status in the pending line: still running.
        Assert.Equal(CheckState.Running, HookChecks.ParseHooks("", Dots("pytest") + "Pass").Single().State);
    }

    [Fact]
    public void A_pytest_progress_line_of_the_start_width_is_no_hook()
    {
        var pytest = "tests/test_a.py " + new string('.', 73 - "tests/test_a.py ".Length) + "   [ 40%]";
        Assert.Equal(73, pytest.IndexOf(' ', 16)); // dots up to the start width, as long as Dots("pytest")
        var hooks = HookChecks.ParseHooks(Line("pytest", "Failed") + "\n- hook id: pytest\n\n" + pytest + "\n=== 1 failed in 0.10s ===\n");
        Assert.Equal(("pytest", CheckState.Failed), (hooks.Single().Name, hooks.Single().State));
        Assert.EndsWith("[ 40%]\n=== 1 failed in 0.10s ===\n", hooks[0].Output);
        Assert.Empty(HookChecks.ParseHooks("", pytest)); // nor as the pending line
    }

    [Fact]
    public void A_wrapped_pytest_progress_line_of_the_start_width_is_no_hook()
    {
        var wrapped = "s" + new string('.', 72) + "F.sxX";
        Assert.Equal(73, wrapped.IndexOf('F')); // the dots end at the start width, then pytest's result letters
        var hooks = HookChecks.ParseHooks(Line("pytest", "Failed") + "\n\n" + wrapped + "\n" + "s" + new string('.', 72) + "E\n");
        Assert.Equal("pytest", hooks.Single().Name);
        Assert.Empty(HookChecks.ParseHooks("", "s" + new string('.', 72) + "F"));
        Assert.Equal("pytest", HookChecks.ParseHooks("", Dots("pytest") + "Fa").Single().Name); // a status being written
    }

    [Fact]
    public void A_long_run_of_dots_is_parsed_quickly()
    {
        var dots = new string('.', 20_000);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var hooks = HookChecks.ParseHooks(dots + "\n" + "x" + dots + "Passed\n" + Line("ruff", "Passed") + "\n", "x" + dots);
        Assert.True(sw.ElapsedMilliseconds < 200, $"{sw.ElapsedMilliseconds} ms");
        Assert.Equal(("ruff", CheckState.Passed), (hooks.Single().Name, hooks.Single().State));
    }

    [Fact]
    public void Plain_merge_has_no_hooks()
    {
        const string text = "$ git merge b --no-ff\n\nMerge made by the 'ort' strategy.\n a.txt | 2 +-\n 1 file changed, 1 insertion(+), 1 deletion(-)\n";
        Assert.Empty(HookChecks.ParseHooks(text, ""));
        Assert.Null(HookChecks.ParsePytestSummary(text));
    }

    [Fact]
    public void Terminal_progress()
    {
        Assert.Empty(HookChecks.ParseTty("")!);
        Assert.Equal([("test_a.py", CheckState.Passed), ("test_b.py", CheckState.Failed), ("test_c.py", CheckState.Running)],
            HookChecks.ParseTty("test_a.py ✓ test_b.py ✗ test_c.py ")!.Select(m => (m.Name, m.State)));
        Assert.Equal([("test_a.py", CheckState.Running)], HookChecks.ParseTty("test_a.py ")!.Select(m => (m.Name, m.State)));
        Assert.Empty(HookChecks.ParseTty("test_")!); // still being written
        // One item per line works too; colours and carriage returns are ignored.
        Assert.Equal([("test_a.py", CheckState.Passed), ("test_b.py", CheckState.Running)],
            HookChecks.ParseTty("\x1b[1mtest_a.py\x1b[0m \x1b[32m✓\x1b[0m\r\ntest_b.py ")!.Select(m => (m.Name, m.State)));
        // Not that pattern: shown raw by the caller.
        Assert.Null(HookChecks.ParseTty("Enter passphrase: x "));
        Assert.Null(HookChecks.ParseTty("✓ "));
        Assert.Equal("a\nb\nc\nd", HookChecks.CleanTty("\x1b[31ma\x1b[0m\r\nb\rc\r\r\nd"));
        // Private CSI (cursor off/on), OSC (window title, ended by BEL or ST), charset selection.
        Assert.Equal("test_a.py ✓ ", HookChecks.CleanTty("\x1b[?25l\x1b]0;tests\u0007test_a.py \x1b]2;x\x1b\\\x1b(B✓ \x1b[?25h"));
        Assert.Equal([("test_a.py", CheckState.Passed)], HookChecks.ParseTty("\x1b[?25ltest_a.py \x1b(B✓ \x1b[?25h")!.Select(m => (m.Name, m.State)));
    }
}
