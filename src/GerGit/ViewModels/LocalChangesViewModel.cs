using System.Diagnostics;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls.Selection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;
using GerGit.Core.Diff;
using GerGit.Core.Git;
using GerGit.ViewModels.Dialogs;
using GerGit.Views;

namespace GerGit.ViewModels;

/// <summary>One row of the Unstaged or Staged list: a file, or a folder in tree mode.</summary>
public sealed class FileRow(string path, string text, int depth, bool isTree, StatusEntry? entry, bool isExpanded = true)
{
    /// <summary>Relative path of the file or folder.</summary>
    public string Path { get; } = path;
    public string Text { get; } = text;
    public int Depth { get; } = depth;
    public bool IsTree { get; } = isTree;
    public StatusEntry? Entry { get; } = entry;
    public bool IsExpanded { get; } = isExpanded;

    public bool IsFolder => Entry is null;

    /// <summary>An untracked nested git repository (`?? dir/`): one row, no diff, never discarded.</summary>
    public bool IsNestedRepository => Entry is { } e && GitRepository.IsNestedRepository(e);
    public ChangeKind Kind => Entry?.Kind ?? ChangeKind.Modified;
    public Thickness Indent => new(Depth * 10, 0, 0, 0);
    public string ChevronIcon => IsExpanded ? "chevron-down" : "chevron-right";

    // Limitation: no MIME theme icons; every file uses Lucide `file`.
    public string FileIcon => IsFolder ? "folder" : Entry!.IsSubmodule ? "package" : IsNestedRepository ? "folder-git-2" : "file";

    /// <summary>"Status icons" notes: renamed and copied rows always have the old/new tooltip; the other rows
    /// get the full text only when it is truncated (TrimmedToolTip on the text).</summary>
    public string? ToolTip => Entry is { OldPath: { } old } e && old != e.Path ? $"Old:\t{old}\nNew:\t{e.Path}" : null;
}

/// <summary>List = the list with the selection; FirstFile = the same after the first-file fallback, but only when the
/// view is shown and the user is not typing in the commit box; Subject = the subject field.</summary>
public enum LocalChangesFocus { List, FirstFile, Subject }

/// <summary>
/// The Local Changes view of one repository tab: Unstaged / Staged lists, file operations, the diff of the
/// selected file and the commit box.
/// Not built yet: a filter box (Ctrl+F), Hide Untracked / Show Ignored, a side-by-side popup (Space), drag and drop,
/// live splitter snapping, saved widths and view mode, working-tree watching (window activation, F5 and the index
/// watcher cover it), submodule discard, .gitignore / LFS / stash helpers, external tools.
/// </summary>
public sealed partial class LocalChangesViewModel : ObservableObject
{
    /// <summary>Length indicator (Preferences > Commit); read on each use.</summary>
    public static int SubjectLowLimit => AppPreferences.SubjectWarnAt;
    public static int SubjectHighLimit => AppPreferences.SubjectErrorAt;

    readonly HashSet<string> _collapsedUnstaged = [], _collapsedStaged = [];
    IReadOnlyList<StatusEntry> _unstaged = [], _staged = [];
    bool _updating;     // selection changes made by the view model itself
    bool _amendQuiet;   // amend switched off after a commit: no draft restore
    int _statusReads;   // generation of OnStatusReadAsync: an older read that resumes late is dropped
    int _amendSwitches; // generation of the amend switch
    bool _stagedIsAmend;     // the shown Staged list is an amend list
    bool _amendErrorShown;   // a failing amend list read shows its error once, until a read succeeds or amend is switched
    string? _seenAmendSha;   // the rebase edit stop Amend was last auto-checked for
    (string Subject, string Description)? _draft;

    public LocalChangesViewModel(RepoTabViewModel owner)
    {
        Owner = owner;
        Diff = new DiffViewModel(owner.Repo) { LineActionHandler = OnLineActionAsync, LayoutKey = DiffViewModel.LocalChangesLayoutKey };
        UnstagedSelection = new SelectionModel<FileRow> { SingleSelect = false, Source = UnstagedRows };
        StagedSelection = new SelectionModel<FileRow> { SingleSelect = false, Source = StagedRows };
        UnstagedSelection.SelectionChanged += (_, _) => OnUserSelection(UnstagedSelection, StagedSelection);
        StagedSelection.SelectionChanged += (_, _) => OnUserSelection(StagedSelection, UnstagedSelection);
        owner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(RepoTabViewModel.Refs) or nameof(RepoTabViewModel.IsLoaded)) Notify();
        };
        LoadPreparedMessage();
    }

    public RepoTabViewModel Owner { get; }
    GitRepository Repo => Owner.Repo;
    public DiffViewModel Diff { get; }

    public AvaloniaList<FileRow> UnstagedRows { get; } = [];
    public AvaloniaList<FileRow> StagedRows { get; } = [];
    public SelectionModel<FileRow> UnstagedSelection { get; }
    public SelectionModel<FileRow> StagedSelection { get; }

    /// <summary>Asks a question with a primary and a cancel button; true = primary. Replaceable in tests.</summary>
    internal Func<string, string, string, string, Task<bool>> Ask { get; set; } =
        (title, text, primary, cancel) => MessageWindow.ShowAsync(title, text, primary, cancel, 550);

    /// <summary>The error beep of a refused stage; replaceable in tests.
    /// Limitation: Console.Beep (BEL on the terminal, if any); no desktop sound-theme event.</summary>
    internal Action Beep { get; set; } = Console.Beep;

    /// <summary>The view moves the focus.</summary>
    public event Action<LocalChangesFocus>? FocusRequested;

    /// <summary>The last amend switch (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>A stage / unstage / discard / apply job or a commit runs.</summary>
    [ObservableProperty] public partial bool Busy { get; private set; }

    /// <summary>Shift held anywhere in the view: header buttons read "Stage All" / "Unstage All".</summary>
    [ObservableProperty] public partial bool ShiftHeld { get; set; }

    /// <summary>View Mode: Tree (default) or List. Not persisted.</summary>
    [ObservableProperty] public partial bool IsTreeMode { get; set; } = true;

    [ObservableProperty] public partial string Subject { get; set; } = "";
    [ObservableProperty] public partial string Description { get; set; } = "";
    [ObservableProperty] public partial bool Amend { get; set; }

    bool UnstagedSelected => UnstagedSelection.SelectedIndexes.Count > 0;
    bool StagedSelected => StagedSelection.SelectedIndexes.Count > 0;

    public bool ListsEnabled => !Busy;
    public bool IsSingleSelection => UnstagedSelection.SelectedIndexes.Count + StagedSelection.SelectedIndexes.Count == 1;

    // Toolbar and list headers.
    bool StageAllMode => UnstagedRows.Count > 0;
    public bool StageAllEnabled => !Busy && (UnstagedRows.Count > 0 || StagedRows.Count > 0);
    public string StageAllIcon => StageAllMode || StagedRows.Count == 0 ? "chevrons-down" : "chevrons-up";
    public string StageAllToolTip => StageAllMode || StagedRows.Count == 0 ? "Stage All" : "Unstage All";
    public string StageHeaderText => ShiftHeld ? "Stage All" : "Stage";
    public bool StageHeaderEnabled => !Busy && (ShiftHeld ? UnstagedRows.Count > 0 : UnstagedSelected);
    public string UnstageHeaderText => ShiftHeld ? "Unstage All" : "Unstage";
    public bool UnstageHeaderEnabled => !Busy && (ShiftHeld ? StagedRows.Count > 0 : StagedSelected);

    // "Commit box", "Commit button text", "When may the user commit".
    public string CounterText => (SubjectLowLimit - Subject.Length).ToString();
    public bool CounterVisible => Subject.Length > 0;
    public bool CounterWarn => Subject.Length > SubjectLowLimit && Subject.Length <= SubjectHighLimit;
    public bool CounterError => Subject.Length > SubjectHighLimit;
    public string CounterToolTip => $"The recommended subject line should be {SubjectLowLimit} characters or less";

    /// <summary>Preferences closed: the counter shows the new length limits, and the commit button and its menu the
    /// "Push automatically on commit" setting, at once.</summary>
    public void OnSubjectLimitsChanged()
    {
        Notify();
        OnPropertyChanged(nameof(CounterToolTip));
    }

    RepoState State => Owner.State;

    /// <summary>What the commit button runs; the click does
    /// exactly what the text says, so text and click both come from this.</summary>
    enum CommitAction { Commit, ContinueRebase, ContinueCherryPick, ContinueAm }

    /// <summary>"Commit button text", first match wins (Amend checked = a normal commit).</summary>
    CommitAction Action =>
        Amend ? CommitAction.Commit
        : State.Kind == RepoStateKind.Rebase && (State.AmendSha is null || WorkDirClean) ? CommitAction.ContinueRebase
        : State.Kind == RepoStateKind.CherryPickSequence && WorkDirClean ? CommitAction.ContinueCherryPick
        : State.Kind == RepoStateKind.ApplyPatches ? CommitAction.ContinueAm
        : CommitAction.Commit;

    /// <summary>No staged and no tracked unstaged file other than submodules (untracked files do not count).</summary>
    bool WorkDirClean => _staged.All(e => e.IsSubmodule) && _unstaged.All(e => e.IsSubmodule || e.Kind is ChangeKind.Untracked or ChangeKind.Ignored);

    /// <summary>Push automatically on commit (Preferences > General).</summary>
    public const string PushAfterCommitKey = "general.pushAfterCommit";

    public static bool PushAfterCommit => Settings.Get(PushAfterCommitKey, false);

    /// <summary>A push may follow this commit, and the "Commit ..." text has the drop-down arrow (commit button text, commit-and-push
    /// setting): not Amend, Continue, Squash or a rebase. Further exclusions (the
    /// click does what the text says):
    /// - not on a detached HEAD: there is no branch to push;
    /// - not in a cherry-pick sequence, also while its pick is stopped at a conflict: a half-finished sequence is never
    ///   pushed. A single cherry-pick is complete with this commit, so it may push;
    /// - not in a revert, single or not: excluded (a revert usually needs a look before it leaves);
    /// - no " and Push" on "Continue Cherry-Pick" / "Continue".</summary>
    public bool HasCommitMenu => !Amend && Action == CommitAction.Commit && !Owner.Refs.IsDetached && !State.InSequence
        && State.Kind is not (RepoStateKind.Squash or RepoStateKind.Rebase or RepoStateKind.CherryPickSequence or RepoStateKind.Revert);

    /// <summary>Commit-and-push is active: the setting XOR Shift.</summary>
    bool CommitAndPush => HasCommitMenu && (PushAfterCommit ^ ShiftHeld);

    /// <summary>Commit Options menu: the item does the opposite of the setting.</summary>
    public string CommitMenuText => PushAfterCommit ? "Commit" : "Commit and Push";
    public bool InvertedPush => !PushAfterCommit;

    public string CommitText => CommitAndPush ? BaseCommitText + " and Push" : BaseCommitText;

    string BaseCommitText => Amend ? "Amend Last Commit" : Action switch
    {
        CommitAction.ContinueRebase => "Continue Rebase",
        CommitAction.ContinueCherryPick => "Continue Cherry-Pick",
        CommitAction.ContinueAm => "Continue",
        _ when State.Kind == RepoStateKind.Squash => "Squash",
        _ => _staged.Count switch
        {
            0 => "Commit",
            1 => "Commit 1 File",
            var n => $"Commit {n} Files",
        },
    };

    /// <summary>When may the user commit: in merge, rebase, sequence, squash and am states only "no unmerged
    /// file left"; otherwise something staged (or Amend) and a subject.</summary>
    public bool CanCommit => Owner.IsLoaded && !Busy && (State.Kind is RepoStateKind.Merge or RepoStateKind.Rebase
            or RepoStateKind.CherryPickSequence or RepoStateKind.Squash or RepoStateKind.ApplyPatches
        ? !_unstaged.Any(e => e.Kind == ChangeKind.Conflicted)
        : (_staged.Count > 0 || Amend) && !string.IsNullOrWhiteSpace(Subject));

    /// <summary>Locked states: a rebase stop without amend target (git fixes the message) and am lock the
    /// message fields and Amend.</summary>
    bool MessageLocked => State.Kind == RepoStateKind.Rebase && State.AmendSha is null || State.Kind == RepoStateKind.ApplyPatches;
    public bool MessageEnabled => !MessageLocked;

    /// <summary>138 instead of 235 in any conflict-type state.</summary>
    public double DescriptionMaxHeight => State.Kind is RepoStateKind.None or RepoStateKind.Bisect ? 235 : 138;

    /// <summary>Amend needs a HEAD commit (design choice: disabled on an unborn HEAD, nothing to amend).</summary>
    public bool CanAmend => !Busy && !MessageLocked && Owner.Refs.HeadSha is not null;

    /// <summary>Warnings (commit box): only while committing is allowed and no rebase is in progress.</summary>
    public string? Warning =>
        !CanCommit || State.Kind == RepoStateKind.Rebase ? null
        : Owner.Refs is { HeadSha: not null, IsDetached: true } ? "Repository is in detached HEAD state"
        : Amend && Owner.Refs.Refs.FirstOrDefault(r => r.FullName == Owner.Refs.ActiveBranch) is { UpstreamState: UpstreamState.Valid } b
            && !(b.Ahead > 0 && b.Behind == 0) ? "Amending commit that has already been pushed"
        : null;

    public bool HasWarning => Warning is not null;

    void Notify()
    {
        foreach (var name in (string[])[nameof(ListsEnabled), nameof(IsSingleSelection), nameof(StageAllEnabled), nameof(StageAllIcon),
                     nameof(StageAllToolTip), nameof(StageHeaderText), nameof(StageHeaderEnabled), nameof(UnstageHeaderText),
                     nameof(UnstageHeaderEnabled), nameof(CounterText), nameof(CounterVisible), nameof(CounterWarn), nameof(CounterError),
                     nameof(CommitText), nameof(HasCommitMenu), nameof(CommitMenuText), nameof(InvertedPush), nameof(CanCommit),
                     nameof(CanAmend), nameof(Warning), nameof(HasWarning),
                     nameof(MessageEnabled), nameof(DescriptionMaxHeight), nameof(UnmergedSelected), nameof(ResolveOursText),
                     nameof(ResolveTheirsText), nameof(ResetToUnmergedVisible), nameof(ResetToUnmergedEnabled)])
            OnPropertyChanged(name);
    }

    partial void OnBusyChanged(bool value) => Notify();
    partial void OnShiftHeldChanged(bool value) => Notify();
    partial void OnSubjectChanged(string value) => Notify();

    partial void OnIsTreeModeChanged(bool value)
    {
        Refill(false);
        Refill(true);
    }

    [RelayCommand] void SetTreeMode() => IsTreeMode = true;
    [RelayCommand] void SetListMode() => IsTreeMode = false;

    public void RequestFocus(LocalChangesFocus target) => FocusRequested?.Invoke(target);

    // ---- Lists ----

    /// <summary>
    /// Called by the tab after every successful status read (the selected file's diff reloads even when the
    /// status did not change). The lists are rebuilt only when their entries changed.
    /// </summary>
    public async Task OnStatusReadAsync(StatusSnapshot status, CancellationToken ct = default)
    {
        int gen = ++_statusReads;
        bool amend = Amend;
        IReadOnlyList<StatusEntry> staged = status.Staged;
        if (amend)
        {
            try { staged = await Repo.ReadAmendStagedAsync(ct); }
            catch (GitException e) when (!ct.IsCancellationRequested)
            {
                if (gen != _statusReads || Amend != amend) return;
                // Keep the shown amend list. While switching on there is none: switch back, never show the normal list
                // under "Amend Last Commit" (its unstage would run the wrong procedure).
                bool show = !_amendErrorShown;
                _amendErrorShown = true;
                if (!_stagedIsAmend) Amend = false;
                if (show) await Owner.ShowError("Git Error", e.Message);
                return;
            }
            ct.ThrowIfCancellationRequested();
            // A newer read or an amend switch happened meanwhile: this list belongs to the other mode or is older.
            if (gen != _statusReads || Amend != amend) return;
            _amendErrorShown = false;
        }
        _stagedIsAmend = amend;
        if (status.Unstaged.SequenceEqual(_unstaged) && staged.SequenceEqual(_staged)) ShowDiff();
        else Rebuild(status.Unstaged, staged);
        Notify();
    }

    static (bool IsFolder, string Path) Key(FileRow r) => (r.IsFolder, r.Path);

    static HashSet<(bool, string)> Keys(SelectionModel<FileRow> sel) => sel.SelectedItems.OfType<FileRow>().Select(Key).ToHashSet();

    static bool SelectKeys(SelectionModel<FileRow> sel, IList<FileRow> rows, HashSet<(bool, string)> keys)
    {
        bool any = false;
        for (int i = 0; i < rows.Count; i++)
            if (keys.Contains(Key(rows[i]))) { sel.Select(i); any = true; }
        return any;
    }

    /// <summary>Selection rules: restore by path, else the next file row near the old position, else the first file.</summary>
    void Rebuild(IReadOnlyList<StatusEntry> unstaged, IReadOnlyList<StatusEntry> staged)
    {
        _unstaged = unstaged;
        _staged = staged;
        bool inStaged = StagedSelected;
        var (sel, rows) = inStaged ? (StagedSelection, StagedRows) : (UnstagedSelection, UnstagedRows);
        var keys = Keys(sel);
        int last = sel.SelectedIndexes.Count > 0 ? sel.SelectedIndexes.Max() : -1;
        bool firstFile = false;
        _updating = true;
        try
        {
            Fill(UnstagedRows, unstaged, _collapsedUnstaged);
            Fill(StagedRows, staged, _collapsedStaged);
            UnstagedSelection.Clear();
            StagedSelection.Clear();
            if (keys.Count > 0 && !SelectKeys(sel, rows, keys) && NextFileRow(rows, last) is { } next) sel.Select(next);
            if (!UnstagedSelected && !StagedSelected
                && (SelectFirstFile(UnstagedSelection, UnstagedRows) || SelectFirstFile(StagedSelection, StagedRows)))
                firstFile = true;
        }
        finally { _updating = false; }
        SelectionUpdated();
        if (firstFile) RequestFocus(LocalChangesFocus.FirstFile); // "selected and focused"
    }

    /// <summary>First file row at most 10 rows from <paramref name="last"/> on, else the closest at most 10 back, else the last file row.</summary>
    static int? NextFileRow(IList<FileRow> rows, int last)
    {
        for (int i = last; i < Math.Min(rows.Count, last + 10); i++)
            if (!rows[i].IsFolder) return i;
        for (int i = Math.Min(last - 1, rows.Count - 1); i >= Math.Max(0, last - 10); i--)
            if (!rows[i].IsFolder) return i;
        for (int i = rows.Count - 1; i >= 0; i--)
            if (!rows[i].IsFolder) return i;
        return null;
    }

    static bool SelectFirstFile(SelectionModel<FileRow> sel, IList<FileRow> rows)
    {
        for (int i = 0; i < rows.Count; i++)
            if (!rows[i].IsFolder) { sel.Select(i); return true; }
        return false;
    }

    void Fill(AvaloniaList<FileRow> rows, IReadOnlyList<StatusEntry> entries, HashSet<string> collapsed)
    {
        rows.Clear();
        rows.AddRange(BuildRows(entries, IsTreeMode, collapsed));
    }

    /// <summary>Rebuilds one list (view mode, expand/collapse) and keeps its selection by path.</summary>
    void Refill(bool staged, FileRow? fallback = null)
    {
        var (rows, sel, entries, collapsed) = staged
            ? (StagedRows, StagedSelection, _staged, _collapsedStaged)
            : (UnstagedRows, UnstagedSelection, _unstaged, _collapsedUnstaged);
        var keys = Keys(sel);
        _updating = true;
        try
        {
            Fill(rows, entries, collapsed);
            sel.Clear();
            // A collapsed folder takes over the selection of its hidden rows.
            if (!SelectKeys(sel, rows, keys) && keys.Count > 0 && fallback is not null) SelectKeys(sel, rows, [Key(fallback)]);
        }
        finally { _updating = false; }
        SelectionUpdated();
    }

    /// <summary>
    /// List view modes: tree = folders before files at every level, then name, then change type; list = full
    /// path. Both culture-aware. Folders are expanded unless their path is in <paramref name="collapsed"/>.
    /// </summary>
    internal static List<FileRow> BuildRows(IReadOnlyList<StatusEntry> entries, bool tree, IReadOnlySet<string> collapsed)
    {
        var cmp = StringComparer.CurrentCulture;
        if (!tree)
            return entries.OrderBy(e => e.Path, cmp).ThenBy(e => e.Kind).Select(e => new FileRow(e.Path, e.Path.TrimEnd('/'), 0, false, e)).ToList();

        var rows = new List<FileRow>();
        Add("", 0, entries);
        return rows;

        void Add(string prefix, int depth, IEnumerable<StatusEntry> items)
        {
            // A nested repository's path ends in "/": it is a row of its own, not a folder.
            var split = items.Select(e => (Entry: e, Slash: e.Path.TrimEnd('/').IndexOf('/', prefix.Length))).ToList();
            foreach (var folder in split.Where(x => x.Slash >= 0).GroupBy(x => x.Entry.Path[prefix.Length..x.Slash]).OrderBy(g => g.Key, cmp))
            {
                var path = prefix + folder.Key;
                bool open = !collapsed.Contains(path);
                rows.Add(new FileRow(path, folder.Key, depth, true, null, open));
                if (open) Add(path + "/", depth + 1, folder.Select(x => x.Entry));
            }
            foreach (var e in split.Where(x => x.Slash < 0).Select(x => x.Entry).OrderBy(e => e.Path[prefix.Length..], cmp).ThenBy(e => e.Kind))
                rows.Add(new FileRow(e.Path, e.Path[prefix.Length..].TrimEnd('/'), depth, true, e));
        }
    }

    /// <summary>Expands or collapses a folder row (chevron, Right / Left).</summary>
    public void ToggleFolder(FileRow row, bool? expand = null)
    {
        bool staged = StagedRows.Contains(row);
        if (!row.IsFolder || (!staged && !UnstagedRows.Contains(row))) return;
        bool open = expand ?? !row.IsExpanded;
        if (open == row.IsExpanded) return;
        var collapsed = staged ? _collapsedStaged : _collapsedUnstaged;
        if (open) collapsed.Remove(row.Path);
        else collapsed.Add(row.Path);
        Refill(staged, row);
    }

    /// <summary>Only one list holds a selection.</summary>
    void OnUserSelection(SelectionModel<FileRow> sel, SelectionModel<FileRow> other)
    {
        if (_updating) return;
        if (sel.SelectedIndexes.Count > 0 && other.SelectedIndexes.Count > 0)
        {
            _updating = true;
            try { other.Clear(); }
            finally { _updating = false; }
        }
        SelectionUpdated();
    }

    void SelectionUpdated()
    {
        ShowDiff();
        Notify();
    }

    /// <summary>
    /// Diff of the first selected row in display order; a folder or nothing = empty diff.
    /// Calls Show with an equal request on every refresh, so the diff reloads and keeps its scroll position.
    /// </summary>
    void ShowDiff()
    {
        bool staged = StagedSelected;
        var (sel, rows) = staged ? (StagedSelection, StagedRows) : (UnstagedSelection, UnstagedRows);
        if (sel.SelectedIndexes.Count == 0 || rows[sel.SelectedIndexes.Min()] is not { Entry: { } e, IsNestedRepository: false })
        {
            SetCard(null);
            Diff.Show(null, actions: LineActionSet.None);
            return;
        }
        if (!staged && e.Kind == ChangeKind.Conflicted)
        {
            ShowCard(e);
            return;
        }
        SetCard(null);
        var working = staged ? (Amend ? WorkingDiff.StagedAmend : WorkingDiff.Staged)
            : e.Kind is ChangeKind.Untracked or ChangeKind.Ignored ? WorkingDiff.Untracked : WorkingDiff.Unstaged;
        // Submodules get no line buttons. Type changes
        // (symlink <-> file) have two diff sections and the parser keeps only the first, so the buttons would act on a
        // hidden half: none until the diff shows both.
        // The set goes with the request: the diff view takes it only together with the loaded diff.
        var actions = e.IsSubmodule || e.Kind is ChangeKind.Conflicted or ChangeKind.TypeChanged ? LineActionSet.None
            : staged ? LineActionSet.Unstage : LineActionSet.StageDiscard;
        Diff.Show(new DiffRequest(null, "", e.Path, e.OldPath, Working: working), actions: actions);
    }

    /// <summary>Staging rules in one place: selected files, folders replaced by all files below them, no duplicates.</summary>
    List<StatusEntry> SelectedEntries(bool staged)
    {
        var (sel, entries) = staged ? (StagedSelection, _staged) : (UnstagedSelection, _unstaged);
        var seen = new HashSet<string>();
        var result = new List<StatusEntry>();
        foreach (var r in sel.SelectedItems.OfType<FileRow>())
            foreach (var e in r.Entry is { } one ? [one] : entries.Where(x => x.Path.StartsWith(r.Path + "/", StringComparison.Ordinal)))
                if (seen.Add(e.Path)) result.Add(e);
        return result;
    }

    /// <summary>"Stash N File(s)...": the distinct paths of the expanded selection of one list.</summary>
    public IReadOnlyList<string> StashPaths(bool staged) => SelectedEntries(staged).Select(e => e.Path).Distinct().ToList();

    /// <summary>Copy Path / Copy Full Path: the selected rows of the active list, one per line.</summary>
    public string CopyText(bool full)
    {
        var sel = StagedSelected ? StagedSelection : UnstagedSelection;
        return string.Join("\n", sel.SelectedItems.OfType<FileRow>().Select(r => full ? System.IO.Path.Combine(Owner.Path, r.Path) : r.Path));
    }

    // ---- Conflict card ----

    /// <summary>The conflict card shown instead of the diff while an unmerged Unstaged row is selected; else null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CardUnresolved), nameof(CardResolved), nameof(DiffVisible))]
    public partial ConflictCardViewModel? Card { get; private set; }

    public bool CardUnresolved => Card is { IsResolved: false };
    public bool CardResolved => Card is { IsResolved: true };
    public bool DiffVisible => !CardUnresolved;

    /// <summary>The last resolved-state check of the card (tests await it).</summary>
    public Task CardLoading { get; private set; } = Task.CompletedTask;

    void SetCard(ConflictCardViewModel? card)
    {
        if (Card == card) return;
        if (Card is not null) Card.PropertyChanged -= OnCardChanged;
        Card = card;
        if (card is not null) card.PropertyChanged += OnCardChanged;
    }

    void OnCardChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConflictCardViewModel.IsResolved)) return;
        foreach (var name in (string[])[nameof(CardUnresolved), nameof(CardResolved), nameof(DiffVisible)]) OnPropertyChanged(name);
    }

    /// <summary>The card for this unmerged entry; kept over a refresh while file and sides are the same. The resolved
    /// state is read again every time (the user may have edited the markers away).</summary>
    void ShowCard(StatusEntry e)
    {
        var file = State.Unmerged.FirstOrDefault(f => f.Path == e.Path) ?? new UnmergedFile(e.Path, 'U', 'U', e.IsSubmodule);
        var card = Card is { } c && c.Matches(State, file) ? c : new ConflictCardViewModel(this, State, file);
        SetCard(card);
        ShowCardDiff(card);
        CardLoading = LoadResolvedAsync(card);
    }

    /// <summary>Resolved: the diff against the ours (stage 2) version below the strip; else no diff.</summary>
    void ShowCardDiff(ConflictCardViewModel card) => Diff.Show(
        card.IsResolved ? new DiffRequest(null, "", card.Path, null, Working: WorkingDiff.Ours) : null, actions: LineActionSet.None);

    async Task LoadResolvedAsync(ConflictCardViewModel card)
    {
        try
        {
            bool resolved = await Repo.IsConflictResolvedAsync(card.File);
            if (Card != card) return;
            bool changed = card.IsResolved != resolved;
            card.IsResolved = resolved;
            if (changed) ShowCardDiff(card);
        }
        catch (Exception e) { Trace.WriteLine(e); }
    }

    /// <summary>At least one selected Unstaged row is unmerged (menu items "Resolve Using '...'").</summary>
    public bool UnmergedSelected => SelectedEntries(false).Any(e => e.Kind == ChangeKind.Conflicted);
    public string ResolveOursText => $"Resolve Using '{State.OursName}'";
    public string ResolveTheirsText => $"Resolve Using '{State.TheirsName}'";

    [RelayCommand] Task ResolveOurs() => ResolveSelectedAsync(true);
    [RelayCommand] Task ResolveTheirs() => ResolveSelectedAsync(false);

    /// <summary>"Resolve using" / 08 card buttons: every selected unmerged Unstaged file, in order, stopping at
    /// the first error (Core); then a full refresh, errors in the Git Error window.</summary>
    internal Task ResolveSelectedAsync(bool ours)
    {
        var state = State;
        var paths = SelectedEntries(false).Where(e => e.Kind == ChangeKind.Conflicted).Select(e => e.Path).ToList();
        if (paths.Count == 0) return Task.CompletedTask;
        return RunGitJobAsync($"Resolve Using '{(ours ? state.OursName : state.TheirsName)}'",
            async job => await Repo.ResolveWithSideAsync(job, state, paths, ours));
    }

    /// <summary>Staged menu "Reset File to Unmerged State...": any state but clean; one non-folder row.</summary>
    public bool ResetToUnmergedVisible => State.Kind != RepoStateKind.None;
    public bool ResetToUnmergedEnabled => StagedSelection.SelectedItems is [FileRow { IsFolder: false }];

    /// <summary>Reset file to unmerged state: confirmation, then `checkout -m` as a job, then a refresh.</summary>
    [RelayCommand]
    async Task ResetToUnmerged()
    {
        try
        {
            if (Busy || !ResetToUnmergedVisible || StagedSelection.SelectedItems is not [FileRow { IsFolder: false } row]) return;
            if (!await Owner.Confirm("Do you want reset file to unmerged state?",
                    "This will reset conflict resolution and return the file to the unmerged state.", "Unmerge", 500))
                return;
            await RunGitJobAsync("Reset File to Unmerged State", async job => await Repo.ResetToUnmergedAsync(job, row.Path));
        }
        catch (Exception e) { Trace.WriteLine(e); } // reached from the menu's command binding: never throw
    }

    /// <summary>A git operation as a job of the tab (refresh, Git Error window) with the lists and commit box locked.</summary>
    internal async Task RunGitJobAsync(string name, Func<Job, Task<GitResult?>> work, bool focusList = true)
    {
        if (Busy) return;
        Busy = true;
        try { await Owner.RunJobAsync(name, work); }
        catch (Exception e) { Trace.WriteLine(e); }
        finally { Busy = false; }
        if (focusList) RequestFocus(LocalChangesFocus.List);
    }

    // ---- File operations ----

    [RelayCommand] Task Stage() => StageEntriesAsync(SelectedEntries(false));
    [RelayCommand] Task StageAll() => StageEntriesAsync(_unstaged);
    [RelayCommand] Task Unstage() => UnstageEntriesAsync(SelectedEntries(true));
    [RelayCommand] Task UnstageAll() => UnstageEntriesAsync(_staged);

    /// <summary>Enter / Ctrl+S / double-click: unstage when the Staged list holds the selection, else stage.</summary>
    [RelayCommand] Task Toggle() => StagedSelected ? Unstage() : Stage();

    /// <summary>Header buttons: with Shift held they act on the whole list.</summary>
    [RelayCommand] Task StageHeader() => ShiftHeld ? StageAll() : Stage();
    [RelayCommand] Task UnstageHeader() => ShiftHeld ? UnstageAll() : Unstage();

    /// <summary>The toolbar button: Stage All while the Unstaged list has rows, else Unstage All.</summary>
    [RelayCommand] Task StageOrUnstageAll() => StageAllMode ? StageAll() : UnstageAll();

    /// <summary>Ctrl+Shift+Alt+S: by the list that holds the selection, else like the toolbar button.</summary>
    [RelayCommand]
    Task StageAllShortcut() => StagedSelected ? UnstageAll() : UnstagedSelected ? StageAll() : StageOrUnstageAll();

    /// <summary>Stage files: a refused stage gate (unresolved conflict among the files) stages nothing and beeps.</summary>
    Task StageEntriesAsync(IReadOnlyList<StatusEntry> entries) => entries.Count == 0 ? Task.CompletedTask
        : RunAsync(async () =>
        {
            if (!await Repo.StageAsync(entries.Select(e => e.Path).ToList())) Beep();
        }, focusList: true);

    Task UnstageEntriesAsync(IReadOnlyList<StatusEntry> entries)
    {
        bool amend = Amend;
        return entries.Count == 0 ? Task.CompletedTask : RunAsync(() => Repo.UnstageAsync(entries.ToList(), amend), focusList: true);
    }

    /// <summary>"Discard files" with the "Confirm: discard files" dialog. Not built yet: submodule discard.</summary>
    [RelayCommand]
    async Task Discard()
    {
        try
        {
            if (Busy) return;
            // Nested repositories are never discarded (Core skips them too) and are not counted.
            var files = SelectedEntries(false).Where(e => !e.IsSubmodule && !GitRepository.IsNestedRepository(e)).ToList();
            if (files.Count == 0) return;
            if (!await Ask("Discard changes", "Do you want to discard all your changes in the selected files?",
                    files.Count == 1 ? "Discard" : $"Discard Changes in {files.Count} Files", "Cancel"))
                return;
            await RunAsync(() => Repo.DiscardAsync(files), focusList: true);
        }
        catch (Exception e) { Trace.WriteLine(e); } // reached from the menu's command binding: never throw
    }

    /// <summary>
    /// Runs one stage-type job: lists and commit box locked, then a full refresh and, on failure, the "Git Error" window.
    /// Never throws (it is reached from async void handlers).
    /// </summary>
    async Task RunAsync(Func<Task> job, bool focusList)
    {
        if (Busy) return;
        Busy = true;
        string? error = null;
        try { await job(); }
        catch (Exception e)
        {
            if (e is not GitException) Trace.WriteLine(e);
            error = e.Message;
        }
        // Limitation: always the full refresh, no per-path status refresh.
        try { await RefreshFullyAsync(); }
        finally { Busy = false; }
        if (focusList) RequestFocus(LocalChangesFocus.List);
        if (error is not null) await Owner.ShowError("Git Error", error);
    }

    /// <summary>
    /// A full refresh, and any newer one that replaced it (git's index write wakes the watcher, whose refresh cancels
    /// ours): the lists, the selection and the commit button are current when this returns.
    /// </summary>
    async Task RefreshFullyAsync()
    {
        await Owner.RefreshAsync();
        while (!Owner.Refreshing.IsCompleted) await Owner.Refreshing;
    }

    /// <summary>
    /// The diff pane's stage / unstage / discard of lines. The patch is built before
    /// the first await: a refresh during the confirmation would replace the diff the line indices point into.
    /// A failing stage / unstage shows the "Git Error" window (a silent failure is a bug).
    /// </summary>
    async Task OnLineActionAsync(PatchAction action, IReadOnlyList<HunkLine> lines)
    {
        try
        {
            if (Busy || Diff.FileDiff is not { } diff || PatchBuilder.Build(diff, lines, action) is not { } patch) return;
            if (action == PatchAction.Discard)
            {
                int n = ChangedLines(patch);
                if (!await Ask("Discard changes", "Do you want to discard all your changes in the selected lines?",
                        n == 1 ? "Discard Line" : $"Discard {n} Lines", "Cancel"))
                    return;
            }
            await RunAsync(() => Repo.ApplyPatchAsync(patch, action), focusList: false);
        }
        catch (Exception e) { Trace.WriteLine(e); }
    }

    /// <summary>Added + deleted lines of a patch (the body starts at the first hunk header).</summary>
    internal static int ChangedLines(string patch) =>
        patch.Split('\n').SkipWhile(l => !l.StartsWith("@@", StringComparison.Ordinal)).Count(l => l.StartsWith('+') || l.StartsWith('-'));

    // ---- Commit box ----

    /// <summary>
    /// Called by the tab after every state read. A new state kind discards the draft and reloads the message; a rebase edit stop checks Amend when it is first seen.
    /// </summary>
    internal void OnStateRead(RepoState old, RepoState state)
    {
        if (old.Kind != state.Kind) ResetMessage(uncheckAmend: false);
        // A locked state (rebase conflict stop, am) can still find Amend on (ticked before, or an edit stop's auto-amend
        // followed by a conflicting pick): the user could not untick it and the button would amend mid-rebase.
        if (MessageLocked && Amend) ResetMessage(uncheckAmend: true);
        if (state.AmendSha is { } stop && stop != _seenAmendSha && !Amend) Amend = true;
        _seenAmendSha = state.AmendSha;
        Notify();
    }

    /// <summary>Erases the draft and fills the box with git's prepared message (or empties it); after an abort, a
    /// continue or a state-kind change. <paramref name="uncheckAmend"/>: Amend off without restoring the old draft.
    /// With Amend still on the fields keep HEAD's message and the prepared message becomes the draft that unticking
    /// restores.</summary>
    internal void ResetMessage(bool uncheckAmend)
    {
        if (uncheckAmend && Amend)
        {
            _amendQuiet = true;
            Amend = false;
            _amendQuiet = false;
        }
        var prepared = Repo.ReadPreparedMessage() is { } m ? CommitMessage.Split(m) : ("", "");
        _draft = Amend ? prepared : null;
        if (!Amend) (Subject, Description) = prepared;
    }

    /// <summary>Message prepared by git: fills an empty box.</summary>
    void LoadPreparedMessage()
    {
        if (Subject.Length > 0 || Description.Length > 0) return;
        if (Repo.ReadPreparedMessage() is { } m) (Subject, Description) = CommitMessage.Split(m);
    }

    partial void OnAmendChanged(bool value)
    {
        _statusReads++; // a read still waiting for the list of the other mode must not apply it
        _amendErrorShown = false;
        Notify();
        if (!_amendQuiet) Loading = ApplyAmendAsync(value);
    }

    /// <summary>Amend: on = save the draft and load HEAD's message, off = restore the draft; the Staged list
    /// switches between HEAD's parent → index and HEAD → index. Limitation: the draft lives in memory only.</summary>
    async Task ApplyAmendAsync(bool on)
    {
        int gen = ++_amendSwitches;
        try
        {
            if (on)
            {
                _draft = (Subject, Description);
                var message = await Repo.ReadHeadMessageAsync();
                if (gen != _amendSwitches) return; // switched again meanwhile
                (Subject, Description) = CommitMessage.Split(message);
            }
            else if (_draft is { } d)
            {
                (Subject, Description) = d;
                _draft = null;
            }
            await OnStatusReadAsync(Owner.Status);
        }
        catch (Exception e)
        {
            Trace.WriteLine(e);
            await Owner.ShowError("Git Error", e.Message);
        }
    }

    /// <summary>Ctrl+Enter / Ctrl+Shift+Enter / the commit button / the Commit Options item. It does what the text says. <paramref name="push"/>: the quick push follows the commit; null =
    /// as the button shows it (the setting XOR Shift). Never after Amend, Squash, a rebase or a continue, whoever asks.</summary>
    [RelayCommand]
    async Task Commit(bool? push)
    {
        if (!CanCommit) return;
        bool andPush = HasCommitMenu && (push ?? (PushAfterCommit ^ ShiftHeld));
        switch (Action)
        {
            case CommitAction.ContinueRebase:
                await ContinueAsync("Continue Rebase", Repo.ContinueRebaseAsync);
                return;
            case CommitAction.ContinueCherryPick:
                await ContinueAsync("Continue Cherry-Pick", Repo.ContinueCherryPickAsync);
                return;
            case CommitAction.ContinueAm:
                await ContinueAsync("Continue Am", Repo.ContinueAmAsync);
                return;
        }
        CloseChecks(); // a new commit supersedes the last checks window and its Skip
        var message = CommitMessage.Build(Subject, Description);
        bool amend = Amend, noVerify = false;
        Busy = true;
        try
        {
            // The button must show what git commits. If the index changed behind the shown list (an outside
            // tool, a refresh still on its way), do not commit; refresh so the button shows the real state, and the user
            // clicks again.
            var staged = amend ? await Repo.ReadAmendStagedAsync() : (await Repo.ReadStatusAsync(lockFree: true)).Staged;
            if (!staged.SequenceEqual(_staged))
            {
                await RefreshFullyAsync();
                return;
            }
            var pushBranch = andPush ? await Repo.ResolveBranchAsync() : null; // the push goes to the committed branch only
            while (true)
            {
                GitResult r;
                ChecksDialogViewModel? checks = null; // the checks window, once a hook shows (not for the skip commit)
                CommitSnapshot? snapshot = null;
                bool headMoved = false, red = false; // red: a hook failed (post-commit after a commit; then no push)
                try
                {
                    if (noVerify) red = HasRedHook(r = await Repo.CommitAsync(message, amend, noVerify));
                    else
                    {
                        var job = new Job("Commit", hidden: true);
                        snapshot = new(await Repo.ResolveHeadAsync(), await Repo.ResolveBranchAsync(), staged, message, amend, andPush);
                        var vm = new ChecksDialogViewModel($"{Owner.Title} — Commit — Checks") { RunningJob = job, IsBusy = true };
                        try
                        {
                            r = await vm.Checks.RunAsync(job, () => Repo.CommitAsync(message, amend, noVerify: false, job: job), () =>
                            {
                                if (Owner.ShowDialog(vm).IsCompleted) return; // not shown (no main window): today's way
                                checks = vm;
                                _checksWindow = vm;
                            });
                        }
                        finally
                        {
                            vm.IsBusy = false; // the window can always be closed
                            vm.RunningJob = null;
                        }
                        red = vm.Checks.Hooks.Any(h => h.IsFailed); // also when the window was not shown
                        if (r.Cancelled) headMoved = await Repo.ResolveHeadAsync() != snapshot.Head; // an amend moves HEAD too
                        if (_tabClosed)
                        {
                            vm.Close(false);
                            return;
                        }
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { r = new GitResult(-1, "", e.Message); }
                bool committed = r.ExitCode == 0 || (r.Cancelled && headMoved);
                var hookFailure = checks is not null && ShowChecksResult(checks, r, committed);
                if (hookFailure && snapshot is not null)
                {
                    // The button shows only together with its action (before the refresh below, a click meanwhile works).
                    checks!.Skip = () => SkipAsync(checks, snapshot);
                    checks.SubmitText = "Skip pre-commit hooks and commit";
                }
                if (committed)
                {
                    // No push after a cancel that came after the commit, or a red post-commit hook (red = not pushed).
                    await CommittedAsync(r.Cancelled || red ? null : pushBranch);
                    return;
                }
                // Failure: the fields stay (the draft is kept), everything refreshes, then the error window.
                await RefreshFullyAsync();
                if (checks is { KeepOpen: true })
                {
                    // The checks window shows it (a cancel or a red hook) and stays open; Local Changes is usable again.
                    // Its "Skip pre-commit hooks and commit" (a red hook, set above) replaces the Git Error question below.
                    return;
                }
                if (noVerify || !await Repo.HasCommitHooksAsync())
                {
                    await Owner.ShowError("Git Error", r.ReadableOutput);
                    return;
                }
                if (!await Ask("Git Error", r.ReadableOutput, "Skip pre-commit hooks and commit", "Close")) return;
                noVerify = true;
            }
        }
        catch (GitException e) { await Owner.ShowError("Git Error", e.Message); } // e.g. the staged-list re-read failed
        catch (Exception e) { Trace.WriteLine(e); }
        finally { Busy = false; }
    }

    /// <summary>What a commit with checks ran against: Skip commits only when all of it is still so.</summary>
    sealed record CommitSnapshot(string? Head, string? Branch, IReadOnlyList<StatusEntry> Staged, string Message, bool Amend, bool Push);

    ChecksDialogViewModel? _checksWindow; // the open checks window of this tab (at most one)
    bool _tabClosed;

    /// <summary>Closes the checks window (a new commit supersedes it; its Skip no longer applies); a commit still running
    /// in it is cancelled first. <paramref name="tabClosed"/>: the tab closes, a running commit closes it when it ends.</summary>
    internal void CloseChecks(bool tabClosed = false)
    {
        _tabClosed |= tabClosed;
        _checksWindow?.Cancel(); // busy: cancels the commit; else closes
        _checksWindow = null;
    }

    /// <summary>The quick push after the last commit-and-push (tests await it).</summary>
    internal Task Pushing { get; private set; } = Task.CompletedTask;

    /// <summary>Today's success path: draft erased, amend unticked, fields cleared, everything refreshed; then, for commit
    /// and push, the quick push of <paramref name="pushBranch"/> (the committed branch, full ref; null = no push). It is not awaited: Local Changes is usable while it runs.</summary>
    async Task CommittedAsync(string? pushBranch)
    {
        _draft = null;
        _amendQuiet = true;
        Amend = false; // without reloading the message
        _amendQuiet = false;
        (Subject, Description) = ("", "");
        LoadPreparedMessage();
        await RefreshFullyAsync();
        if (pushBranch is not null) (Pushing = Owner.RemoteOps.QuickPushAsync(pushBranch)).Forget();
    }

    /// <summary>
    /// "Skip pre-commit hooks and commit" in the checks window: the window may have been open
    /// for a while, so it commits only when HEAD, the branch, the staged set and the message are still those of the
    /// checked commit; else nothing is committed and the user is told to commit again. Then the retry of "Commit failed".
    /// </summary>
    async Task SkipAsync(ChecksDialogViewModel window, CommitSnapshot snap)
    {
        if (_checksWindow == window) _checksWindow = null;
        if (_tabClosed) return;
        if (Busy)
        {
            await Owner.ShowError("Commit", BusyTryAgain);
            return;
        }
        Busy = true;
        try
        {
            var staged = snap.Amend ? await Repo.ReadAmendStagedAsync() : (await Repo.ReadStatusAsync(lockFree: true)).Staged;
            if (await Repo.ResolveHeadAsync() != snap.Head || await Repo.ResolveBranchAsync() != snap.Branch
                || !staged.SequenceEqual(snap.Staged) || Amend != snap.Amend || CommitMessage.Build(Subject, Description) != snap.Message)
            {
                await RefreshFullyAsync();
                await Owner.ShowError("Commit", ChangedSinceChecks);
                return;
            }
            GitResult r;
            try { r = await Repo.CommitAsync(snap.Message, snap.Amend, noVerify: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { r = new GitResult(-1, "", e.Message); }
            if (r.Success)
            {
                await CommittedAsync(snap.Push && !HasRedHook(r) ? snap.Branch : null); // a red post-commit hook: no push
                return;
            }
            await RefreshFullyAsync();
            await Owner.ShowError("Git Error", r.ReadableOutput);
        }
        catch (GitException e) { await Owner.ShowError("Git Error", e.Message); }
        catch (Exception e) { Trace.WriteLine(e); }
        finally { Busy = false; }
    }

    /// <summary>A pre-commit hook line with "Failed" in the output of a commit without the checks panel (the skip
    /// commit runs no pre-commit hooks, so it can only be post-commit's): parsed like the checks panel does.</summary>
    static bool HasRedHook(GitResult r) => HookChecks.ParseHooks(r.ReadableOutput).Any(h => h.State == CheckState.Failed);

    internal const string ChangedSinceChecks = "The repository changed since the checks ran — commit again";
    internal const string BusyTryAgain = "Local Changes is busy — try again";

    /// <summary>Delay before the checks window of a green commit closes by itself; read while it runs (tests change it).</summary>
    internal static TimeSpan ChecksCloseDelay { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The result in the checks window of a commit whose hooks showed. Green
    /// and committed: closes by itself after <see cref="ChecksCloseDelay"/> unless the user touched it. Committed with a red
    /// hook (post-commit), a cancel, or a red hook before the commit: stays open (KeepOpen); for the last one the caller
    /// adds the skip.
    /// Any other failure: closed, the caller shows today's error. True = a red hook stopped the commit.
    /// </summary>
    static bool ShowChecksResult(ChecksDialogViewModel vm, GitResult r, bool committed)
    {
        var failed = vm.Checks.Hooks.Where(h => h.IsFailed).Select(h => h.Name).ToList();
        if (committed && failed.Count == 0)
        {
            vm.SetStatus(DialogStatus.Success, "Committed, checks passed");
            _ = vm.CloseLaterAsync(() => ChecksCloseDelay);
        }
        else if (committed) vm.SetStatus(DialogStatus.Warning, $"Committed, but checks failed: {string.Join(", ", failed)}");
        else if (r.Cancelled)
            // The group's SIGINT ends git with the hook before the commit: HEAD and the index stay as they were.
            vm.SetStatus(DialogStatus.Warning, "Cancelled during checks — not committed; your message is kept");
        else if (failed.Count > 0)
        {
            vm.SetStatus(DialogStatus.Error, $"Checks failed: {string.Join(", ", failed)} — not committed; your message is kept");
        }
        else
        {
            vm.Close(false);
            return false;
        }
        vm.KeepOpen = !(committed && failed.Count == 0);
        vm.CancelText = "Close";
        return !committed && !r.Cancelled && failed.Count > 0;
    }

    /// <summary>Continue: the state's continue command as a job; on success the draft is erased, Amend
    /// unticked and the box emptied (before the job's refresh, which may already show the next stop).</summary>
    async Task ContinueAsync(string name, Func<Job, Task<GitResult>> run) => await RunGitJobAsync(name, async job =>
    {
        var r = await run(job);
        if (r.Success) ResetMessage(uncheckAmend: true);
        return r;
    });
}
