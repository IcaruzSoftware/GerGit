using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.ViewModels.Dialogs;

/// <summary>
/// The checks panel (Merge dialog, commit checks window): the hooks git ran (pre-commit's status lines in the job output, the running one
/// from the pending line), a hook's items from its progress on the terminal, pytest's summary line under its hook and
/// the raw output. <see cref="Update"/> re-parses everything and updates the rows in place, so they keep their identity.
/// </summary>
public sealed partial class ChecksViewModel : ObservableObject
{
    string? _ttyHook;

    public ObservableCollection<HookRow> Hooks { get; } = [];
    public bool HasHooks => Hooks.Count > 0;

    [ObservableProperty]
    public partial string Output { get; set; } = "";

    /// <summary>The user clicked, focused or typed into the panel or opened its output (set by the view): a window showing
    /// it does not close by itself.</summary>
    public bool Touched { get; set; }

    /// <summary>
    /// Starts <paramref name="run"/> (a git command of <paramref name="job"/> with a pty) and feeds this panel from the
    /// job's output, pending line and terminal text every 150 ms until it ends, then once more as final.
    /// <paramref name="hooksShown"/> runs once, when the first hook appears. The panel never fails the command.
    /// </summary>
    public async Task<GitResult> RunAsync(Job job, Func<Task<GitResult>> run, Action? hooksShown = null)
    {
        int from = job.Output.Length; // the command's own output starts here (its header comes with the run)
        var task = run();
        while (!task.IsCompleted)
        {
            Feed(final: false);
            await Task.WhenAny(task, Task.Delay(150));
        }
        Feed(final: true);
        return await task;

        void Feed(bool final)
        {
            try
            {
                var (output, pending) = job.Snapshot();
                Update(output, from, final ? "" : pending, job.TtyText, final);
                if (HasHooks && hooksShown is { } shown)
                {
                    hooksShown = null;
                    shown();
                }
            }
            catch (Exception e) { Trace.WriteLine(e); }
        }
    }

    /// <summary>Re-parses the hooks in <paramref name="output"/> from <paramref name="from"/> (the merge's own output) and
    /// shows all of it as the raw output, plus <paramref name="tty"/> (the terminal text) when it is no item progress
    /// (<see cref="HookChecks.ParseTty"/>). <paramref name="final"/> = git ended: a hook still running never ends (red);
    /// an item still running is red, or skipped under a passed hook.</summary>
    public void Update(string output, int from, string pending, string tty, bool final = false)
    {
        var items = HookChecks.ParseTty(tty);
        Output = items is null ? $"{output}\n[terminal]\n{HookChecks.CleanTty(tty)}" : output;
        items ??= [];
        var hooks = HookChecks.ParseHooks(output[from..], pending);
        // Limitation: the terminal text names no hook; its items go to the hook seen running when they first show up,
        // else to the last hook then (a hook that starts and ends within one poll is not seen running).
        if (items.Count > 0) _ttyHook ??= hooks.FirstOrDefault(h => h.State == CheckState.Running)?.Name ?? hooks.LastOrDefault()?.Name;
        var owner = _ttyHook;
        int before = Hooks.Count;
        while (Hooks.Count > hooks.Count) Hooks.RemoveAt(Hooks.Count - 1);
        for (int i = 0; i < hooks.Count; i++)
        {
            if (i == Hooks.Count) Hooks.Add(new HookRow());
            var (row, hook) = (Hooks[i], hooks[i]);
            row.Name = hook.Name;
            row.State = Ended(hook.State, CheckState.Failed);
            row.Note = HookChecks.ParsePytestSummary(hook.Output);
            // An item still running when its hook passed did not count (skipped); under any other hook it is red.
            var unfinished = row.State == CheckState.Passed ? CheckState.Skipped : CheckState.Failed;
            row.SetItems(hook.Name == owner ? [.. items.Select(m => m with { State = Ended(m.State, unfinished) })] : []);
        }
        if (Hooks.Count != before) OnPropertyChanged(nameof(HasHooks));

        CheckState Ended(CheckState state, CheckState unfinished) => final && state == CheckState.Running ? unfinished : state;
    }
}

/// <summary>A row of the checks panel: name and state (spinner, green check, red x, grey skip).</summary>
public partial class CheckRow : ObservableObject
{
    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunning), nameof(IsPassed), nameof(IsFailed), nameof(IsSkipped), nameof(Icon))]
    public partial CheckState State { get; set; }

    public bool IsRunning => State == CheckState.Running;
    public bool IsPassed => State == CheckState.Passed;
    public bool IsFailed => State == CheckState.Failed;
    public bool IsSkipped => State == CheckState.Skipped;

    public string? Icon => State switch
    {
        CheckState.Passed => "circle-check",
        CheckState.Failed => "circle-x",
        CheckState.Skipped => "circle-slash",
        _ => null,
    };
}

public sealed partial class HookRow : CheckRow
{
    public ObservableCollection<CheckRow> Items { get; } = [];

    /// <summary>pytest's summary line in the hook's output, such as "1 failed, 3 passed in 0.12s".</summary>
    [ObservableProperty]
    public partial string? Note { get; set; }

    internal void SetItems(IReadOnlyList<ItemCheck> items)
    {
        while (Items.Count > items.Count) Items.RemoveAt(Items.Count - 1);
        for (int i = 0; i < items.Count; i++)
        {
            if (i == Items.Count) Items.Add(new CheckRow());
            (Items[i].Name, Items[i].State) = (items[i].Name, items[i].State);
        }
    }
}

/// <summary>
/// The checks window of a commit from Local Changes and of a push with a
/// pre-push hook: no body, the checks panel below the buttons. Not modal. Busy
/// (Cancel cancels the commit or push) while it runs; then the result in the footer.
/// </summary>
public sealed class ChecksDialogViewModel : DialogViewModel
{
    public ChecksDialogViewModel(string title)
    {
        Title = title;
        SubmitText = null;
        Width = 760; // the result line next to "Skip pre-commit hooks and commit" and Close
        MinWidth = 420;
        MinHeight = 200;
        CanResize = true;
        Modal = false; // a test run can take minutes: the user keeps browsing
        CancelIsDefault = true; // Enter (and Esc) never run "Skip pre-commit hooks and commit"; that takes a click
        Panel = Checks;
    }

    public ChecksViewModel Checks { get; } = new();

    /// <summary>"Skip pre-commit hooks and commit" after a red hook: the window closes, then this runs.</summary>
    internal Func<Task>? Skip { get; set; }

    /// <summary>A green result: closes after <paramref name="delay"/> (read while it waits; tests change it) unless the
    /// user clicked, focused or typed into the panel or opened its output meanwhile.</summary>
    internal async Task CloseLaterAsync(Func<TimeSpan> delay)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < delay() && !Checks.Touched && Result is null) await Task.Delay(100);
        if (!Checks.Touched) Close(false);
    }

    protected override async Task OnSubmitAsync()
    {
        if (Skip is not { } skip) return; // no action yet: the button does nothing
        Close(true);
        await skip();
    }
}
