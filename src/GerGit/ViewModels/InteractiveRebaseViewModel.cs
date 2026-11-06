using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.Core.Rebase;
using GerGit.ViewModels.Dialogs;
using GerGit.ViewModels.Dialogs.Refs;

namespace GerGit.ViewModels;

/// <summary>One item of the action dropdown. Action null = the Move Up / Move Down commands.</summary>
public sealed record ActionChoice(RebaseAction? Action, string Title, string Description, string Shortcut, IBrush? Dot, bool Up = false)
{
    // Same in both themes.
    static readonly IBrush Green = new ImmutableSolidColorBrush(Color.Parse("#34C84A"));
    static readonly IBrush Yellow = new ImmutableSolidColorBrush(Color.Parse("#FDBE41"));
    static readonly IBrush Gray = new ImmutableSolidColorBrush(Color.Parse("#E1E1E1"));
    static readonly IBrush Red = new ImmutableSolidColorBrush(Color.Parse("#FA4B49"));

    /// <summary>Index = (int)RebaseAction for the six actions, then Move Up, Move Down.</summary>
    public static readonly IReadOnlyList<ActionChoice> All =
    [
        new(RebaseAction.Pick, "Pick", "Use Commit", "P", Green),
        new(RebaseAction.Edit, "Edit", "Stop for amending", "E", Yellow),
        new(RebaseAction.Reword, "Reword", "Edit the commit message", "R", Yellow),
        new(RebaseAction.Squash, "Squash", "Meld commit into previous one and keep message", "S", Gray),
        new(RebaseAction.Fixup, "Fixup", "Meld commit into previous one and discard message", "F", Gray),
        new(RebaseAction.Drop, "Drop", "Remove commit", "D", Red),
        new(null, "Move Up", "", "Ctrl+↑", null, Up: true),
        new(null, "Move Down", "", "Ctrl+↓", null),
    ];

    public bool HasDot => Dot is not null;
}

/// <summary>One row of the todo list as shown. Rebuilt from <see cref="RebaseTodo.Rows"/>.</summary>
public sealed record RebaseItem(RebaseRow Row, bool ShowRefs)
{
    public int ActionIndex => (int)Row.Action;
    public ActionChoice Choice => ActionChoice.All[ActionIndex];
    public RebaseGlyph Glyph => Row.Glyph;
    public string Subject => Row.Subject;
    public string Author => Row.AuthorName;
    public string AuthorEmail => Row.AuthorEmail;
    public string ShortSha => Row.ShortSha;
    public string Date => Row.AuthorDate.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>Branch chips, only while "Update dependent branches" is checked.</summary>
    public IReadOnlyList<string> Refs => ShowRefs ? Row.Refs.Select(r => r.StartsWith("refs/heads/", StringComparison.Ordinal) ? r[11..] : r).ToList() : [];

    /// <summary>Grey text: squash, fixup and drop rows.</summary>
    public bool IsMuted => Row.Action is RebaseAction.Squash or RebaseAction.Fixup or RebaseAction.Drop;
    public bool IsDropped => Row.Action == RebaseAction.Drop;
}

/// <summary>The reword popup for row <see cref="Row"/>.</summary>
public sealed partial class RewordViewModel(int row, string subject, string description) : ObservableObject
{
    public int Row { get; } = row;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CounterText), nameof(CounterVisible), nameof(CounterWarn), nameof(CounterError))]
    public partial string Subject { get; set; } = subject;

    [ObservableProperty]
    public partial string Description { get; set; } = description;

    // Counter = low limit - length, coloured like the commit box.
    public string CounterText => (LocalChangesViewModel.SubjectLowLimit - Subject.Length).ToString(CultureInfo.InvariantCulture);
    public bool CounterVisible => Subject.Length > 0;
    public bool CounterWarn => Subject.Length > LocalChangesViewModel.SubjectLowLimit && Subject.Length <= LocalChangesViewModel.SubjectHighLimit;
    public bool CounterError => Subject.Length > LocalChangesViewModel.SubjectHighLimit;

    /// <summary>Subject, and the description after one empty line.</summary>
    public string Message => Description.Length == 0 ? Subject : Subject + "\n\n" + Description;

    /// <summary>
    /// Paste into the subject: multi-line text is split at the first line break, the first line replaces the selection
    /// [<paramref name="start"/>, <paramref name="end"/>), the rest becomes the description. Returns the caret, or null
    /// for single-line text (the box pastes it itself).
    /// </summary>
    public int? PasteIntoSubject(string text, int start, int end)
    {
        int nl = text.IndexOf('\n');
        if (nl < 0) return null;
        var first = text[..nl].TrimEnd('\r');
        start = Math.Clamp(Math.Min(start, end), 0, Subject.Length);
        end = Math.Clamp(Math.Max(start, end), start, Subject.Length);
        Subject = Subject[..start] + first + Subject[end..];
        Description = text[(nl + 1)..];
        return start + first.Length;
    }
}

/// <summary>
/// The interactive rebase window: drives one <see cref="InteractiveRebaseSession"/>
/// as a tab job (watcher paused, refresh afterwards), holds the todo list, the selection, the reword popup and the
/// preview. The window closes when git has finished (<see cref="CloseRequested"/>).
/// </summary>
public sealed partial class InteractiveRebaseViewModel : ObservableObject
{
    public const string UpdateRefsKey = "lastUsed.rebase.updateRefs", BackupKey = "lastUsed.rebase.backupRef";

    /// <summary>Opens the window for a started view model; replaceable in tests (drive the view model instead).</summary>
    internal static Func<InteractiveRebaseViewModel, Task> Show { get; set; } = vm => Views.InteractiveRebaseWindow.ShowAsync(vm);

    readonly RepoTabViewModel _tab;
    readonly InteractiveRebaseSession _session;
    readonly QuickAction? _quick;
    readonly IReadOnlyList<string> _quickShas;
    readonly TaskCompletionSource _loaded = new();
    RebaseTodo? _todo;
    Task? _run;
    bool _refreshing, _rewordRevert, _confirming;
    string? _shownSha, _applyError;

    public InteractiveRebaseViewModel(RepoTabViewModel tab, string branch, RefPoint? target, QuickAction? quick = null,
        IReadOnlyList<string>? quickShas = null)
    {
        _tab = tab;
        (Branch, Target, _quick, _quickShas) = (branch, target, quick, quickShas ?? []);
        _session = new InteractiveRebaseSession(tab.Repo, branch, target?.Arg);
        Details = new DetailsViewModel(tab);
        UpdateRefs = Settings.Get(UpdateRefsKey, false);
        Backup = Settings.Get(BackupKey, false);
        Confirm = d => tab.ShowDialog(d);
    }

    public string Branch { get; }

    /// <summary>Null = `--root`: the "On:" chip stays empty.</summary>
    public RefPoint? Target { get; }

    public string? TargetIcon => Target?.Icon;
    public string TargetText => Target?.Text ?? "";

    /// <summary>The preview pane: the details of the first selected row.</summary>
    public DetailsViewModel Details { get; }

    public ObservableCollection<RebaseItem> Items { get; } = [];

    /// <summary>Selected row indices, ascending.</summary>
    public IReadOnlyList<int> Selection { get; private set; } = [];

    /// <summary>The view model changed the selection (move, load): the view selects, scrolls to and focuses the first row.</summary>
    public event Action? SelectionChanged;

    public event Action? CloseRequested;

    /// <summary>git has finished; the window may close.</summary>
    public bool IsClosed { get; private set; }

    /// <summary>Completes when the list is shown, or when git ended without one.</summary>
    public Task Loaded => _loaded.Task;

    /// <summary>The cancel confirmation; the window shows it over itself.</summary>
    internal Func<DialogViewModel, Task<bool>> Confirm { get; set; }

    /// <summary>True until the list is loaded: the "Loading..." placeholder and the footer spinner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanRebase), nameof(ShowSpinner))]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>Rebase or cancel was answered: everything disabled until git ends.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle), nameof(CanRebase), nameof(ShowSpinner))]
    public partial bool IsFinishing { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "";

    [ObservableProperty]
    public partial bool UpdateRefs { get; set; }

    [ObservableProperty]
    public partial bool Backup { get; set; }

    [ObservableProperty]
    public partial RewordViewModel? Reword { get; private set; }

    public bool IsIdle => !IsLoading && !IsFinishing;
    public bool CanRebase => IsIdle && Items.Count > 0;
    public bool ShowSpinner => IsLoading || IsFinishing;

    /// <summary>The list differs from what was loaded (the cancel confirmation rule).</summary>
    public bool Changed => _todo?.Changed == true;

    bool CanEdit => IsIdle && _todo is not null && Reword is null;

    string JobName => Target is null ? $"Interactive rebase '{Branch}'" : $"Interactive rebase '{Branch}' onto '{Target.Text}'";

    partial void OnUpdateRefsChanged(bool value)
    {
        if (_todo is not null) Refresh(Selection);
    }

    // ------------------------------------------------------------------ session

    /// <summary>
    /// Starts git (before the user sees a list) as a job of the tab and runs until git has ended; then the
    /// window closes, the tab refreshes, <paramref name="afterRefresh"/> runs and errors are shown (by the outcome
    /// of the rebase).
    /// </summary>
    public Task RunAsync(Func<Task>? afterRefresh = null) => _run ??= _tab.RunJobAsync(JobName, async job =>
    {
        try
        {
            if (await _session.StartAsync(job) is { } todo) OnLoaded(todo);
            _loaded.TrySetResult();
            var outcome = await _session.Completion;
            return outcome.Error ?? (_applyError is { } e ? new GitResult(1, "", e) : null);
        }
        catch
        {
            // A failure in the window code (load, selection, popup) must not leave git blocked in the helper.
            _session.Cancel();
            try { await _session.Completion; }
            catch (Exception e) { System.Diagnostics.Trace.WriteLine(e); }
            throw;
        }
        finally
        {
            _loaded.TrySetResult();
            Reword = null;
            IsFinishing = true;
            IsClosed = true;
            CloseRequested?.Invoke(); // closed before the refresh and the error dialog
        }
    }, afterRefresh: afterRefresh);

    void OnLoaded(RebaseTodo todo)
    {
        _todo = todo;
        IsLoading = false;
        int? row = _quick is { } q ? todo.ApplyQuickAction(q, _quickShas) : null;
        Refresh(row is { } r ? [r] : todo.Rows.Count > 0 ? [0] : []);
        if (_quick == QuickAction.Reword && row is { } reword) OpenReword(reword);
    }

    /// <summary>`Rebase` (button / Enter): writes the list back and lets git run. Settings are saved first.</summary>
    public async Task RebaseAsync()
    {
        if (!CanRebase || Reword is not null || _todo is null) return;
        IsFinishing = true;
        StatusText = "Rebasing...";
        Settings.Set(UpdateRefsKey, UpdateRefs);
        Settings.Set(BackupKey, Backup);
        try { await _session.ApplyAsync(_todo.Rows, UpdateRefs, Backup); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _applyError = e.Message; // the session cancelled git; shown when it has ended
        }
    }

    /// <summary>`Cancel` / Esc / window close: asks only when the list changed while git waits.</summary>
    public async Task CancelAsync()
    {
        if (IsFinishing || IsClosed || _confirming) return;
        if (_todo is { Changed: true } && _session.Waiting)
        {
            var confirm = new DialogViewModel
            {
                Title = "Do you really want to cancel Interactive Rebase?",
                Description = "All your changes will be discarded.",
                SubmitText = "Yes",
                CancelText = "No",
                Width = 550,
            };
            _confirming = true;
            try
            {
                if (!await Confirm(confirm)) return;
            }
            finally { _confirming = false; }
        }
        Abort();
    }

    /// <summary>Cancels without asking (also when the window could not be shown); nothing once Rebase or cancel was answered.</summary>
    public void Abort()
    {
        if (IsFinishing || IsClosed) return;
        Reword = null;
        IsFinishing = true;
        _session.Cancel();
    }

    // ------------------------------------------------------------------ list editing

    /// <summary>P E S F D (and R with several rows: reword without message, which reverts to pick).</summary>
    public void SetAction(RebaseAction action)
    {
        if (!CanEdit) return;
        _todo!.SetAction(Selection, action);
        Refresh(Selection);
    }

    /// <summary>R: one row selected = the reword popup (cancel sends the row back through the group rules); several = ignored.</summary>
    public void RewordKey()
    {
        if (CanEdit && Selection.Count == 1) OpenReword(Selection[0], revertOnCancel: true);
    }

    /// <summary>An item chosen in row <paramref name="row"/>'s dropdown: applies to the selection if the row is in it, else
    /// to that row alone (which gets selected). Reword on one row opens the popup; Move Up/Down move the rows.</summary>
    public void ChooseAction(int row, ActionChoice choice)
    {
        if (!CanEdit || row < 0 || row >= Items.Count) return;
        if (!Selection.Contains(row)) SetSelection([row], fromView: false);
        if (choice.Action is not { } action)
        {
            if (choice.Up) MoveUp();
            else MoveDown();
        }
        else if (action == RebaseAction.Reword && Selection.Count == 1) OpenReword(row);
        else SetAction(action);
    }

    /// <summary>Ctrl+Up / Move Up.</summary>
    public void MoveUp()
    {
        if (CanEdit) Refresh(_todo!.MoveUp(Selection));
    }

    /// <summary>Ctrl+Down / Move Down.</summary>
    public void MoveDown()
    {
        if (CanEdit) Refresh(_todo!.MoveDown(Selection));
    }

    /// <summary>Drag and drop: the selected rows directly before (upper half) or after (lower half) row <paramref name="target"/>.
    /// Dropping on a dragged row does nothing.</summary>
    public void Drop(int target, bool after)
    {
        if (CanEdit && !Selection.Contains(target)) Refresh(_todo!.MoveTo(Selection, target, after));
    }

    /// <summary>Double-click: the reword popup for pick, edit and reword rows.</summary>
    public void DoubleClick(int row)
    {
        if (!CanEdit || row < 0 || row >= Items.Count) return;
        if (Items[row].Row.Action is not (RebaseAction.Pick or RebaseAction.Edit or RebaseAction.Reword)) return;
        SetSelection([row], fromView: false);
        OpenReword(row);
    }

    /// <summary>Opens the reword popup with the row's current subject and description.</summary>
    public void OpenReword(int row, bool revertOnCancel = false)
    {
        if (!CanEdit || row < 0 || row >= Items.Count) return;
        var r = _todo!.Rows[row];
        _rewordRevert = revertOnCancel;
        Reword = new RewordViewModel(row, r.Subject, r.Description);
    }

    /// <summary>OK: reword with the typed text. Cancel: discard (after R the row goes back through the group rules).</summary>
    public void CloseReword(bool ok)
    {
        if (Reword is not { } popup || _todo is null) return;
        Reword = null;
        if (ok) _todo.Reword(popup.Row, popup.Message.Trim().Length == 0 ? null : popup.Message);
        else if (_rewordRevert) _todo.SetAction([popup.Row], RebaseAction.Reword);
        Refresh([popup.Row]);
    }

    // ------------------------------------------------------------------ selection

    /// <summary>The user changed the list selection.</summary>
    public void SelectRows(IEnumerable<int> rows)
    {
        if (!_refreshing) SetSelection(rows.ToList(), fromView: true);
    }

    void SetSelection(IReadOnlyList<int> rows, bool fromView)
    {
        Selection = rows.Where(i => i >= 0 && i < Items.Count).Distinct().Order().ToList();
        if (!fromView) SelectionChanged?.Invoke();
        // The preview shows the first selected row (DetailsViewModel debounces).
        var sha = Selection.Count > 0 ? Items[Selection[0]].Row.Sha : null;
        if (sha == _shownSha) return;
        _shownSha = sha;
        Details.ShowAsync(sha is null ? [] : [sha]).Forget();
    }

    /// <summary>Items from the todo (changed rows replaced in place, so the list keeps its scroll position), then the selection.</summary>
    void Refresh(IReadOnlyList<int> selection)
    {
        var rows = _todo!.Rows;
        _refreshing = true;
        try
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var item = new RebaseItem(rows[i], UpdateRefs);
                if (i >= Items.Count) Items.Add(item);
                else if (!Items[i].Equals(item)) Items[i] = item;
            }
            while (Items.Count > rows.Count) Items.RemoveAt(Items.Count - 1);
        }
        finally { _refreshing = false; }
        OnPropertyChanged(nameof(CanRebase));
        SetSelection(selection, fromView: false);
    }
}
