using System.Diagnostics;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;
using GerGit.Core.Graph;
using GerGit.Views;

namespace GerGit.ViewModels;

/// <summary>
/// Commit list of one repository tab: streams the revision walk page by page into rows,
/// loads row headers lazily and owns the selection. All members are used on the UI thread.
/// </summary>
public sealed partial class CommitListViewModel(RepoTabViewModel owner) : ObservableObject
{
    // "Pages and thresholds" and "Commit headers (lazy)".
    internal const int PageSize = 10_000, InitialCount = 50_000, LoadMoreDistance = 5_000;
    internal const int HeaderBlock = 100, HeaderCacheBlocks = 200;

    /// <summary>One running revision walk. Pages are read one at a time (CurrentPage serialises the readers).</summary>
    sealed class Walk(IAsyncEnumerator<WalkedCommit> commits, CommitListBuilder builder, CancellationTokenSource cts)
    {
        public readonly IAsyncEnumerator<WalkedCommit> Commits = commits;
        public readonly CommitListBuilder Builder = builder;
        public readonly CancellationTokenSource Cts = cts;
        public bool Done, HasRows, Restored;
        public Task<bool>? CurrentPage;
        public IReadOnlyList<string> PreviousSelection = []; // taken when the first page replaces the old rows
        public int PreviousIndex = -1;
    }

    sealed class HeaderEntry(CommitHeader?[] headers)
    {
        public readonly CommitHeader?[] Headers = headers;
        public long LastUse;
    }

    List<CommitRow> _rows = [];
    readonly Dictionary<string, int> _indexBySha = new(StringComparer.Ordinal);
    Walk? _walk;
    int _generation; // bumped on every rebuild; late header results of an older list are dropped

    readonly Dictionary<int, HeaderEntry> _headers = [];
    readonly HashSet<int> _failedBlocks = [];
    long _clock;
    (int First, int Last) _shown = (-1, -1);
    bool _pumpingHeaders;

    readonly HashSet<int> _selected = [];
    int _anchor = -1;
    bool _selectionTouched; // the user (or the sidebar) selected something after the first page of a reload: no restore

    public RepoTabViewModel Owner { get; } = owner;

    /// <summary>Hands the selection to the details pane; replaceable by tests.</summary>
    internal Func<IReadOnlyList<string>, Task> ShowDetails = shas => owner.Details.ShowAsync(shas);

    /// <summary>Reads one header batch; replaceable by tests.</summary>
    internal Func<IReadOnlyList<string>, Task<IReadOnlyList<CommitHeader>>> ReadHeaders = shas => owner.Repo.ReadHeadersAsync(shas);

    /// <summary>Token of the current walk (tests check that a superseded or closed walk is cancelled).</summary>
    internal CancellationToken WalkToken => _walk?.Cts.Token ?? default;

    /// <summary>Rows loaded so far (grows while paging; replaced on reload).</summary>
    public IReadOnlyList<CommitRow> Rows => _rows;

    /// <summary>Raised when rows, headers or the selection changed (the view redraws).</summary>
    public event Action? Changed;

    /// <summary>Raised to "select and scroll" a row programmatically.</summary>
    public event Action<int>? ScrollIntoViewRequested;

    /// <summary>Raised when a programmatic selection should also give the list keyboard focus.</summary>
    public event Action? FocusRequested;

    /// <summary>SHA of the first selected row (by row order), null when nothing is selected.</summary>
    [ObservableProperty]
    public partial string? SelectedSha { get; set; }

    /// <summary>Row that keyboard navigation starts from (the last clicked or moved-to row).</summary>
    public int Cursor { get; private set; } = -1;

    /// <summary>Selected SHAs ordered by row number (newest first). Passed as is to the details pane.</summary>
    public IReadOnlyList<string> SelectedShas => _selected.Order().Select(i => _rows[i].Sha).ToList();

    public bool IsSelected(int row) => _selected.Contains(row);

    public int IndexOf(string sha) => _indexBySha.GetValueOrDefault(sha, -1);

    // ---------------------------------------------------------------- loading

    /// <summary>
    /// Called on the UI thread after the tab opened and after every refresh that changed refs, HEAD or stashes.
    /// Returns once the first page is shown (and the selection restored, when it is in that page). The rest of the
    /// initial load (until the initial count is reached and HEAD and the old selection are loaded) runs on in the
    /// background. A newer call or <see cref="Cancel"/> stops it.
    /// </summary>
    public async Task LoadAsync(RefSnapshot refs, IReadOnlyList<StashEntry> stashes)
    {
        CancelWalk();
        _refs = refs;
        var cts = new CancellationTokenSource();
        var walk = _walk = new Walk(Owner.Repo.WalkAsync(cts.Token).GetAsyncEnumerator(cts.Token),
            new CommitListBuilder(refs, stashes), cts);
        if (!await LoadPageAsync(walk)) return;
        TryRestore(walk, refs.HeadSha);
        ContinueInitialLoadAsync(walk, refs.HeadSha).Forget();
    }

    async Task ContinueInitialLoadAsync(Walk walk, string? headSha)
    {
        while (!walk.Done && walk == _walk && (_rows.Count < InitialCount || !walk.Restored))
        {
            if (!await LoadPageAsync(walk)) return;
            TryRestore(walk, headSha);
        }
    }

    /// <summary>Tab closed: stops the walk and the search.</summary>
    public void Cancel()
    {
        _searchVersion++; // a debounced search does not start git on a closed tab
        _search?.Cts.Cancel();
        CancelWalk();
    }

    /// <summary>Stops the current walk; cancelling the token also kills the (possibly paused) git process.</summary>
    void CancelWalk()
    {
        if (_walk is not { } walk) return;
        walk.Cts.Cancel();
        if (walk.CurrentPage is not { IsCompleted: false }) walk.Commits.DisposeAsync().AsTask().Forget();
    }

    /// <summary>Restores once HEAD and the old selection are loaded (or the history ended), unless the user selected meanwhile.</summary>
    void TryRestore(Walk walk, string? headSha)
    {
        if (walk.Restored || walk != _walk ||
            !(walk.Done || walk.PreviousSelection.Append(headSha).All(s => s is null || _indexBySha.ContainsKey(s))))
            return;
        walk.Restored = true;
        if (!_selectionTouched) RestoreSelection(walk.PreviousSelection, walk.PreviousIndex, headSha);
    }

    /// <summary>Selection: keep the commits by SHA; a gone commit falls back to the old index; else select HEAD.</summary>
    void RestoreSelection(IReadOnlyList<string> previous, int previousIndex, string? headSha)
    {
        var found = previous.Select(IndexOf).Where(i => i >= 0).ToList();
        if (found.Count > 0)
        {
            SetSelection(found, found[0]);
            ScrollIntoViewRequested?.Invoke(found[0]);
        }
        else if (previousIndex >= 0 && _rows.Count > 0)
            SetSelection([Math.Min(previousIndex, _rows.Count - 1)], Math.Min(previousIndex, _rows.Count - 1));
        else if (headSha is not null && IndexOf(headSha) is var head and >= 0)
            SetSelection([head], head);
        else
            SetSelection([], -1);
    }

    /// <summary>Reads one page of the walk (off the UI thread) and appends its rows. False when cancelled, superseded or failed.</summary>
    Task<bool> LoadPageAsync(Walk walk) =>
        walk.CurrentPage is { IsCompleted: false } running ? running : walk.CurrentPage = ReadPageAsync(walk);

    async Task<bool> ReadPageAsync(Walk walk)
    {
        if (walk.Done || walk.Cts.IsCancellationRequested) return false;
        try
        {
            var (page, done) = await Task.Run(async () =>
            {
                var commits = new List<WalkedCommit>(PageSize);
                while (commits.Count < PageSize && await walk.Commits.MoveNextAsync()) commits.Add(walk.Commits.Current);
                return (walk.Builder.Append(commits), commits.Count < PageSize);
            });
            if (walk != _walk || walk.Cts.IsCancellationRequested) return false;

            walk.Done = done;
            if (!walk.HasRows)
            {
                // First page of a rebuild: replace the list (until now the old rows stayed visible).
                walk.HasRows = true;
                walk.PreviousSelection = SelectedShas;
                walk.PreviousIndex = _selected.Count > 0 ? _selected.Min() : -1;
                _selectionTouched = false;
                _rows = [];
                _indexBySha.Clear();
                _selected.Clear();
                _anchor = Cursor = -1;
                _headers.Clear();
                _failedBlocks.Clear();
                _generation++;
            }
            int start = _rows.Count;
            _rows.AddRange(page);
            for (int i = start; i < _rows.Count; i++) _indexBySha[_rows[i].Sha] = i;
            // Matching: a rebuilt list is searched again; appended rows are searched incrementally.
            if (_search is { } run)
            {
                if (start == 0) Restart(run.Text);
                else Queue(run, start, _rows.Count);
            }
            Changed?.Invoke();
            return true;
        }
        catch (Exception e) when (e is OperationCanceledException || walk.Cts.IsCancellationRequested)
        {
            return false;
        }
        catch (GitException e)
        {
            walk.Done = true;
            await MessageWindow.ShowAsync("Git Error", e.Message);
            return false;
        }
        finally
        {
            if (walk.Done || walk.Cts.IsCancellationRequested) await walk.Commits.DisposeAsync();
        }
    }

    /// <summary>
    /// Called by the view for every frame with the visible row range: loads missing header blocks and,
    /// near the end of the loaded rows, the next page of history.
    /// </summary>
    public void OnRowsShown(int first, int last)
    {
        if (first < 0 || last < first) return;
        _shown = (first, last);
        if (!_pumpingHeaders && NextMissingBlock() >= 0)
        {
            _pumpingHeaders = true;
            Dispatcher.UIThread.Post(() => PumpHeadersAsync().Forget());
        }
        if (_walk is { Done: false, HasRows: true, CurrentPage: null or { IsCompleted: true } } w && last >= _rows.Count - LoadMoreDistance)
            Dispatcher.UIThread.Post(() => LoadPageAsync(w).Forget());
    }

    // ---------------------------------------------------------------- headers

    /// <summary>Header of a row, or null while it is not loaded. Stash rows use the stash data.</summary>
    public CommitHeader? GetHeader(int row)
    {
        var r = _rows[row];
        if (r.Stash is { } s) return new CommitHeader(s.Sha, s.AuthorName, s.AuthorEmail, s.Date, s.ShortSubject, false);
        if (!_headers.TryGetValue(row / HeaderBlock, out var entry)) return null;
        entry.LastUse = ++_clock;
        return entry.Headers[row % HeaderBlock];
    }

    /// <summary>The visible blocks, plus the next block when the last visible row is in the second half of its block (read-ahead).</summary>
    int NextMissingBlock()
    {
        var (first, last) = _shown;
        if (first < 0 || _rows.Count == 0) return -1;
        last = Math.Min(last, _rows.Count - 1);
        int lastBlock = last / HeaderBlock;
        if (last % HeaderBlock >= HeaderBlock / 2 && (lastBlock + 1) * HeaderBlock < _rows.Count) lastBlock++;
        for (int b = first / HeaderBlock; b <= lastBlock; b++)
            if (!_headers.ContainsKey(b) && !_failedBlocks.Contains(b)) return b;
        return -1;
    }

    /// <summary>
    /// Fetches the missing blocks of the current visible range one after the other, so fast scrolling
    /// skips the blocks that are no longer shown instead of starting a git process for each.
    /// </summary>
    async Task PumpHeadersAsync()
    {
        try
        {
            int block;
            while ((block = NextMissingBlock()) >= 0)
            {
                int generation = _generation;
                var rows = _rows.GetRange(block * HeaderBlock, Math.Min(HeaderBlock, _rows.Count - block * HeaderBlock));
                IReadOnlyList<CommitHeader> result;
                try
                {
                    result = await ReadHeaders(rows.Select(r => r.Sha).ToList());
                }
                catch (Exception e)
                {
                    // A failed batch is only logged and its rows stay without text (and it is not retried).
                    Trace.WriteLine($"Commit headers failed: {e}");
                    if (generation == _generation) _failedBlocks.Add(block);
                    continue;
                }
                if (generation != _generation) continue; // list rebuilt meanwhile: drop

                var bySha = new Dictionary<string, CommitHeader>(StringComparer.Ordinal);
                foreach (var h in result) bySha.TryAdd(h.Sha, h);
                _headers[block] = new HeaderEntry(rows.Select(r => bySha.GetValueOrDefault(r.Sha)).ToArray()) { LastUse = ++_clock };
                if (_headers.Count > HeaderCacheBlocks)
                    _headers.Remove(_headers.MinBy(kv => kv.Value.LastUse).Key);
                Changed?.Invoke();
            }
        }
        finally
        {
            _pumpingHeaders = false;
        }
    }

    // ---------------------------------------------------------------- selection

    /// <summary>
    /// Selects the row of this commit and scrolls to it, loading more history if needed. The sidebar keeps its
    /// keyboard focus (focus = false); links such as the parent commits in the details pane move it to the list.
    /// </summary>
    public void SelectCommit(string sha, bool focus = false) => SelectCommits([sha], focus);

    /// <summary>Selects the rows of these commits (several references selected in the sidebar), loading more
    /// history until all are found or the history ends; scrolls to the first.</summary>
    public void SelectCommits(IReadOnlyList<string> shas, bool focus = false)
    {
        _selectionTouched = true;
        if (shas.All(s => IndexOf(s) >= 0)) SelectAndScroll(shas, focus);
        else SelectWhenLoadedAsync(shas, focus).Forget();
    }

    async Task SelectWhenLoadedAsync(IReadOnlyList<string> shas, bool focus)
    {
        while (shas.Any(s => IndexOf(s) < 0) && _walk is { Done: false } w)
            if (!await LoadPageAsync(w)) return;
        SelectAndScroll(shas, focus);
    }

    void SelectAndScroll(IReadOnlyList<string> shas, bool focus)
    {
        var rows = shas.Select(IndexOf).Where(i => i >= 0).Distinct().ToList();
        if (rows.Count == 0) return;
        SetSelection(rows, rows[0]);
        ScrollIntoViewRequested?.Invoke(rows.Min());
        if (focus) FocusRequested?.Invoke();
    }

    /// <summary>Mouse click: plain = only this row, Ctrl = toggle, Shift = range from the anchor.</summary>
    public void Click(int row, bool ctrl, bool shift)
    {
        if (row < 0 || row >= _rows.Count) return;
        _selectionTouched = true;
        if (shift && _anchor >= 0) SelectRange(_anchor, row, ctrl);
        else if (ctrl)
        {
            if (!_selected.Remove(row)) _selected.Add(row);
            _anchor = Cursor = row;
            SelectionChanged();
        }
        else SetSelection([row], row);
    }

    /// <summary>Keyboard move to a row (Up/Down/Page/Home/End); Shift extends from the anchor.</summary>
    public void MoveTo(int row, bool shift)
    {
        if (_rows.Count == 0) return;
        row = Math.Clamp(row, 0, _rows.Count - 1);
        _selectionTouched = true;
        if (shift && _anchor >= 0) SelectRange(_anchor, row, false);
        else SetSelection([row], row);
    }

    void SelectRange(int anchor, int row, bool add)
    {
        if (!add) _selected.Clear();
        for (int i = Math.Min(anchor, row); i <= Math.Max(anchor, row); i++) _selected.Add(i);
        Cursor = row;
        SelectionChanged();
    }

    void SetSelection(IReadOnlyCollection<int> rows, int anchor)
    {
        _anchor = Cursor = anchor;
        if (_selected.SetEquals(rows)) return; // e.g. a click on the only selected row: the details pane keeps its state
        _selected.Clear();
        _selected.UnionWith(rows);
        SelectionChanged();
    }

    /// <summary>The selected rows ordered by row number.</summary>
    public IReadOnlyList<CommitRow> SelectedRows => _selected.Order().Select(i => _rows[i]).ToList();

    // ---------------------------------------------------------------- actions

    /// <summary>Puts text on the clipboard; set by the view, replaceable by tests.</summary>
    internal Func<string, Task> Copy { get; set; } = _ => Task.CompletedTask;

    /// <summary>Copy Commit SHA (Ctrl+C): the full SHAs of the selected rows, by row, one per line.</summary>
    public Task CopyShasAsync() => SelectedShas is { Count: > 0 } shas ? Copy(string.Join("\n", shas)) : Task.CompletedTask;

    /// <summary>Copy Commit Info (Ctrl+Shift+C): "&lt;7-char SHA&gt; - &lt;subject&gt;" per selected row.</summary>
    public async Task CopyInfoAsync()
    {
        var rows = SelectedRows;
        if (rows.Count == 0) return;
        var commits = rows.Where(r => r.Stash is null).Select(r => r.Sha).ToList();
        Dictionary<string, string> subjects;
        try
        {
            subjects = commits.Count == 0 ? [] : (await Owner.Repo.ReadHeadersAsync(commits)).ToDictionary(h => h.Sha, h => h.Subject);
        }
        catch (GitException e)
        {
            await Owner.ShowError("Git Error", e.Message);
            return;
        }
        await Copy(string.Join("\n", rows.Select(r => $"{r.Sha[..7]} - {r.Stash?.ShortSubject ?? subjects.GetValueOrDefault(r.Sha, "")}")));
    }

    /// <summary>F2: rename the selected stash, else the first local branch on the selected row.</summary>
    public Task RenameAsync()
    {
        if (SelectedRows is not [var row]) return Task.CompletedTask;
        if (row.Stash is { } s) return Owner.StashOps.RenameAsync(s);
        return Menus.RowRefs(Owner, row).FirstOrDefault(r => r.Kind == RefKind.LocalBranch) is { } b
            ? Owner.RefOps.RenameBranchAsync(b) : Task.CompletedTask;
    }

    /// <summary>
    /// Delete: all selected rows stashes → delete them; one row → delete its references. There is one delete dialog per kind
    /// (not one for branches and tags together), so the first non-empty group wins:
    /// the row's local branches except the active one (their dialog offers to delete the remote branches too), else
    /// its tags, else its remote branches, but only when the row has no local branch at all (so the active branch's
    /// upstream is never offered on its own).
    /// Not built yet: a combined branches-and-tags delete dialog when it is needed.
    /// </summary>
    public Task DeleteAsync()
    {
        var rows = SelectedRows;
        if (rows.Count > 0 && rows.All(r => r.Stash is not null)) return Owner.StashOps.DeleteAsync(rows.Select(r => r.Stash!).ToList());
        if (rows is not [var row]) return Task.CompletedTask;
        var refs = Menus.RowRefs(Owner, row);
        List<GitRef> Of(RefKind kind) => refs.Where(r => r.Kind == kind).ToList();
        if (Of(RefKind.LocalBranch).Where(r => r.FullName != Owner.Refs.ActiveBranch).ToList() is { Count: > 0 } locals)
            return Owner.RefOps.DeleteBranchesAsync(locals);
        if (Of(RefKind.Tag) is { Count: > 0 } tags) return Owner.RefOps.DeleteTagsAsync(tags);
        if (Of(RefKind.LocalBranch).Count == 0 && Of(RefKind.RemoteBranch) is { Count: > 0 } remotes)
            return Owner.RefOps.DeleteRemoteBranchesAsync(remotes);
        return Task.CompletedTask;
    }

    /// <summary>Ctrl+Shift+B / Ctrl+Shift+G in the list: new branch / tag at the selected commit.</summary>
    public Task NewBranchAsync() => Owner.RefOps.CreateBranchAsync(commitSha: SelectedSha);

    public Task NewTagAsync() => Owner.RefOps.CreateTagAsync(commitSha: SelectedSha);

    /// <summary>
    /// Double-click: on a branch label → checkout that branch; on a row: stash → Apply,
    /// else the first branch label B of the row (nothing when B is the active branch), else "Checkout Commit...".
    /// </summary>
    public Task DoubleClickAsync(int row, RefLabel? label)
    {
        if (row < 0 || row >= _rows.Count) return Task.CompletedTask;
        var r = _rows[row];
        if (r.Stash is { } s) return Owner.StashOps.ApplyAsync(s);
        if (label is not null && label.Kind is not (RefLabelKind.LocalBranch or RefLabelKind.RemoteBranch)) label = null;
        var branch = label ?? r.Labels.FirstOrDefault(l => l.Kind is RefLabelKind.LocalBranch or RefLabelKind.RemoteBranch);
        if (branch is null) return Owner.RefOps.CheckoutCommitAsync(r.Sha);
        if (Owner.Refs.Refs.FirstOrDefault(x => x.FullName == branch.FullName) is not { } b || b.FullName == Owner.Refs.ActiveBranch)
            return Task.CompletedTask;
        return b.Kind == RefKind.LocalBranch ? Owner.RefOps.CheckoutBranchAsync(b) : Owner.RefOps.CheckoutRemoteBranchAsync(b);
    }

    void SelectionChanged()
    {
        var shas = SelectedShas;
        SelectedSha = shas.Count > 0 ? shas[0] : null;
        Changed?.Invoke();
        ShowDetails(shas).Forget();
    }

    // ---------------------------------------------------------------- search

    internal const int SearchChunk = 5_000;
    internal static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(100);

    /// <summary>One search text over the current list: its matches (SHAs) and the chain of batches still to search.</summary>
    sealed class SearchRun(string text)
    {
        public readonly string Text = text;
        public readonly CancellationTokenSource Cts = new();
        public readonly HashSet<string> Matches = new(StringComparer.Ordinal);
        public Task<bool> Tail = Task.FromResult(true); // the last queued batch; false = cancelled or failed
        public Task Jump = Task.CompletedTask; // the jump to the first match after the search (Next / Previous wait for it)
        public int Pending;
    }

    SearchRun? _search;
    int _searchVersion; // bumped on every text change: a debounced search of an older text does not start
    bool _inputPending; // the box text changed and its debounced search has not started yet
    RefSnapshot _refs = new([], null, null);

    /// <summary>Reads the matching commits of one batch; replaceable by tests.</summary>
    internal Func<IReadOnlyList<string>, string, CancellationToken, Task<IReadOnlyList<string>>> SearchCommits =
        (shas, text, ct) => owner.Repo.SearchCommitsAsync(shas, text, ct);

    [ObservableProperty]
    public partial bool IsSearchOpen { get; set; }

    /// <summary>Text of the search box (two-way bound); searched 100 ms after the last change.</summary>
    [ObservableProperty]
    public partial string SearchInput { get; set; } = "";

    /// <summary>A search batch runs (the spinner in the box).</summary>
    [ObservableProperty]
    public partial bool IsSearching { get; private set; }

    /// <summary>`1 match` / `N matches`, empty without a search text.</summary>
    [ObservableProperty]
    public partial string MatchCountText { get; private set; } = "";

    /// <summary>The trimmed text of the active search, "" when there is none (the panel and the details pane highlight it).</summary>
    public string SearchText => _search?.Text ?? "";

    public bool IsMatch(int row) => _search is { } s && s.Matches.Contains(_rows[row].Sha);

    partial void OnSearchInputChanged(string value)
    {
        int version = ++_searchVersion;
        _inputPending = true;
        DelayedAsync().Forget();

        async Task DelayedAsync()
        {
            await Task.Delay(SearchDelay);
            if (version != _searchVersion) return;
            _inputPending = false;
            await SearchAsync(value);
        }
    }

    /// <summary>Enter within 100 ms of typing: the box text is searched first (cancelling its debounced search).</summary>
    async Task SearchPendingInputAsync()
    {
        if (!_inputPending) return;
        _searchVersion++;
        _inputPending = false;
        await SearchAsync(SearchInput);
    }

    public void OpenSearch() => IsSearchOpen = true;

    /// <summary>Esc or the x button: clears the text, the matches and the count; the list gets the focus back.</summary>
    [RelayCommand]
    public void CloseSearch()
    {
        IsSearchOpen = false;
        SearchInput = "";
        _searchVersion++;
        _inputPending = false;
        Restart("");
        FocusRequested?.Invoke();
    }

    /// <summary>Searches the loaded rows, then selects the first match at or after the selection, wrapping once to the top.</summary>
    internal Task SearchAsync(string text) => Restart(text.Trim()) is { } run ? run.Jump = JumpAsync(run) : Task.CompletedTask;

    async Task JumpAsync(SearchRun run)
    {
        if (!await run.Tail || run != _search) return;
        int row = FindMatch(Math.Max(0, SelectionStart), 1);
        if (row < 0) row = FindMatch(0, 1);
        if (row >= 0) SelectMatch(row);
    }

    /// <summary>Next: the first match after the selection; loads pages until one appears or the history ends. No wrap.</summary>
    [RelayCommand]
    public async Task NextMatchAsync()
    {
        await SearchPendingInputAsync();
        if (_search is not { } run) return;
        int? from = null; // rows before it were scanned already (no rescans after each page)
        while (await SearchedAsync(run))
        {
            from ??= SelectionStart + 1; // read after the wait: the finished search may have jumped meanwhile
            if (FindMatch(from.Value, 1) is var row and >= 0)
            {
                SelectMatch(row);
                return;
            }
            from = Math.Max(from.Value, _rows.Count);
            if (_walk is not { Done: false } w || !await LoadPageAsync(w)) return; // the new rows are queued for searching
        }
    }

    /// <summary>Previous: the last loaded match before the selection. No wrap, no paging.</summary>
    [RelayCommand]
    public async Task PreviousMatchAsync()
    {
        await SearchPendingInputAsync();
        if (_search is not { } run || !await SearchedAsync(run)) return;
        if (SelectionStart > 0 && FindMatch(SelectionStart - 1, -1) is var row and >= 0) SelectMatch(row);
    }

    /// <summary>Waits until every queued batch of the run is done (rows may be appended meanwhile). False when it was
    /// cancelled, failed or replaced.</summary>
    async Task<bool> SearchedAsync(SearchRun run)
    {
        await run.Jump;
        Task<bool> tail;
        do
        {
            tail = run.Tail;
            if (!await tail) return false;
        } while (tail != run.Tail);
        return run == _search;
    }

    int SelectionStart => _selected.Count > 0 ? _selected.Min() : -1;

    int FindMatch(int from, int step)
    {
        for (int i = from; i >= 0 && i < _rows.Count; i += step)
            if (IsMatch(i)) return i;
        return -1;
    }

    /// <summary>Selects only the match; the focus stays where it is (in the search box).</summary>
    void SelectMatch(int row)
    {
        _selectionTouched = true;
        SetSelection([row], row);
        ScrollIntoViewRequested?.Invoke(row);
    }

    /// <summary>Cancels the running search and starts one for the text over all loaded rows (none for an empty text).</summary>
    SearchRun? Restart(string text)
    {
        _search?.Cts.Cancel();
        _search = text.Length == 0 ? null : new SearchRun(text);
        if (_search is { } run) Queue(run, 0, _rows.Count);
        OnPropertyChanged(nameof(SearchText));
        UpdateSearchState();
        Changed?.Invoke();
        return _search;
    }

    /// <summary>Searches rows [from, to) after the batches queued before, in chunks; cancellable between chunks.</summary>
    void Queue(SearchRun run, int from, int to)
    {
        if (from >= to) return;
        var shas = _rows.GetRange(from, to - from).Select(r => r.Sha).ToList();
        var previous = run.Tail;
        run.Pending++;
        run.Tail = RunAsync();
        UpdateSearchState();

        async Task<bool> RunAsync()
        {
            string error;
            try
            {
                if (!await previous) return false;
                // References: the short name of every reference (also hidden ones) marks the commit it points to.
                var refShas = _refs.Refs.Where(r => r.ShortName.Contains(run.Text, StringComparison.OrdinalIgnoreCase))
                    .Select(r => r.Sha).ToHashSet(StringComparer.Ordinal);
                for (int i = 0; i < shas.Count; i += SearchChunk)
                {
                    var chunk = shas.GetRange(i, Math.Min(SearchChunk, shas.Count - i));
                    var found = await SearchCommits(chunk, run.Text, run.Cts.Token);
                    if (run.Cts.IsCancellationRequested) return false;
                    run.Matches.UnionWith(found);
                    run.Matches.UnionWith(chunk.Where(refShas.Contains));
                    UpdateSearchState();
                    Changed?.Invoke();
                }
                return true;
            }
            catch (Exception) when (run.Cts.IsCancellationRequested)
            {
                return false;
            }
            catch (Exception e)
            {
                // An error dialog shows the error and the search result stays empty.
                run.Cts.Cancel();
                run.Matches.Clear();
                Changed?.Invoke();
                error = e.Message;
            }
            finally
            {
                run.Pending--;
                UpdateSearchState(); // the spinner stops before the error dialog shows
            }
            await Owner.ShowError("Git Error", error);
            return false;
        }
    }

    void UpdateSearchState()
    {
        IsSearching = _search is { Pending: > 0 };
        MatchCountText = _search is { } s ? s.Matches.Count == 1 ? "1 match" : $"{s.Matches.Count} matches" : "";
    }
}

static class TaskExtensions
{
    /// <summary>Fire and forget, but log a failure instead of losing it.</summary>
    public static async void Forget(this Task task)
    {
        try
        {
            await task;
        }
        catch (Exception e)
        {
            Trace.WriteLine(e);
        }
    }
}
