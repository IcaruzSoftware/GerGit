using System.Globalization;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;
using GerGit.Core.Diff;
using GerGit.Core.Git;

namespace GerGit.ViewModels;

/// <summary>
/// Details pane of one repository tab.
/// Commit and Changes tabs only; no File Tree tab, Quick Look, separate window or collapse toggle.
/// </summary>
public sealed partial class DetailsViewModel(RepoTabViewModel owner) : ObservableObject
{
    public const int CommitTab = 0, ChangesTab = 1;
    const int CommitTabFileLimit = 100; // first 100 files only

    CancellationTokenSource? _cts;
    bool _applying;
    string? _from;
    string _to = "";

    public RepoTabViewModel Owner { get; } = owner;
    public DiffViewModel Diff { get; } = new(owner.Repo) { LayoutKey = DiffViewModel.ChangesLayoutKey };

    /// <summary>The single shown commit (null for ranges, several commits and nothing).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommit), nameof(ShowCommitter), nameof(AuthorDateText), nameof(CommitterDateText),
        nameof(HasDescription), nameof(AuthorColumnSpan), nameof(CommitRefs), nameof(HasRefs), nameof(SingleHeader), nameof(ShowSingleHeader))]
    public partial CommitDetails? Commit { get; private set; }

    /// <summary>True when the single commit is a stash (no SHA row for stash commits).</summary>
    [ObservableProperty]
    public partial bool IsStash { get; private set; }

    /// <summary>Two-commit range: source = older commit (first header row), destination = newer (second header row).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRange), nameof(SourceHeader))]
    public partial CommitDetails? Source { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DestinationHeader))]
    public partial CommitDetails? Destination { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommitTabFiles), nameof(HasFileLimitNote), nameof(FileLimitNote))]
    public partial IReadOnlyList<ChangedFile> Files { get; private set; } = [];

    [ObservableProperty]
    public partial ChangedFile? SelectedFile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSingleHeader), nameof(IsCommitTabActive), nameof(IsChangesTabActive))]
    public partial int SelectedTab { get; set; }

    [ObservableProperty]
    public partial bool IsCommitTabEnabled { get; private set; } = true;

    [ObservableProperty]
    public partial bool IsChangesTabEnabled { get; private set; } = true;

    /// <summary>Overlay over header and tab content; null = no overlay.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverlay))]
    public partial string? OverlayTitle { get; private set; }

    [ObservableProperty]
    public partial string? OverlayText { get; private set; }

    public bool HasOverlay => OverlayTitle is not null;
    public bool HasCommit => Commit is not null;
    public bool IsRange => Source is not null;
    public bool IsCommitTabActive => SelectedTab == CommitTab;
    public bool IsChangesTabActive => SelectedTab == ChangesTab;

    /// <summary>Compact header: hidden for a single commit on the Commit tab, one row on other tabs.</summary>
    public bool ShowSingleHeader => Commit is not null && SelectedTab != CommitTab;

    public HeaderRow? SingleHeader => Commit is null ? null : new(Commit);
    public HeaderRow? SourceHeader => Source is null ? null : new(Source);
    public HeaderRow? DestinationHeader => Destination is null ? null : new(Destination);

    /// <summary>Commit tab: committer block only if name, email or time differ from the author.</summary>
    public bool ShowCommitter => Commit is { } c &&
        (c.CommitterName != c.AuthorName || c.CommitterEmail != c.AuthorEmail || c.CommitterDate != c.AuthorDate);

    /// <summary>Without a committer block the author block spans all four columns (its text: columns 1-3).</summary>
    public int AuthorColumnSpan => ShowCommitter ? 1 : 3;

    public string AuthorDateText => Commit is null ? "" : FullDate(Commit.AuthorDate);
    public string CommitterDateText => Commit is null ? "" : FullDate(Commit.CommitterDate);
    public bool HasDescription => Commit is { Description.Length: > 0 };

    /// <summary>Short names of all refs on the shown commit. Plain pills, not the commit list's coloured labels.</summary>
    public IReadOnlyList<string> CommitRefs => Commit is null ? [] :
        Owner.Refs.Refs.Where(r => r.Sha == Commit.Sha).Select(r => r.ShortName).ToList();

    public bool HasRefs => CommitRefs.Count > 0;

    public IReadOnlyList<ChangedFile> CommitTabFiles => Files.Count > CommitTabFileLimit ? Files.Take(CommitTabFileLimit).ToList() : Files;
    public bool HasFileLimitNote => Files.Count >= CommitTabFileLimit;
    public string FileLimitNote => $"See full list of changed files ({Files.Count}) in the Changes tab";

    // Dates use English month names (invariant culture), local time.
    static string FullDate(DateTimeOffset d) => d.ToLocalTime().ToString("d MMM yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture);

    /// <summary>
    /// Called on every commit-list selection change. <paramref name="shas"/> must be in row order (top row first),
    /// so for two commits shas[1] (lower row, older) is the source and shas[0] the destination.
    /// A newer call cancels a running load.
    /// </summary>
    public Task ShowAsync(IReadOnlyList<string> shas)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        return LoadAsync(shas.ToList(), cts.Token);
    }

    async Task LoadAsync(List<string> shas, CancellationToken ct)
    {
        try
        {
            await Task.Delay(30, ct); // light debounce while the selection moves quickly
            switch (shas.Count)
            {
                case 0:
                    Apply(null, null, null, [], null);
                    break;
                case 1:
                {
                    var details = Owner.Repo.ReadCommitDetailsAsync(shas[0], ct);
                    var files = Owner.Repo.ReadChangedFilesAsync(null, shas[0], ct);
                    await Task.WhenAll(details, files);
                    ct.ThrowIfCancellationRequested();
                    IsStash = Owner.Stashes.Any(s => s.Sha == shas[0]);
                    Apply(details.Result, null, null, files.Result, null);
                    break;
                }
                case 2:
                    await LoadRangeAsync(shas[1], shas[0], ct);
                    break;
                default:
                    Apply(null, null, null, [], null);
                    SetOverlay("Select two commits to see difference between them", $"{shas.Count} commits selected");
                    break;
            }
        }
        catch (Exception e) when (e is OperationCanceledException || ct.IsCancellationRequested) { }
        catch (GitException e)
        {
            Apply(null, null, null, [], null);
            SetOverlay("Error", e.Message);
        }
    }

    async Task LoadRangeAsync(string from, string to, CancellationToken ct)
    {
        var src = Owner.Repo.ReadCommitDetailsAsync(from, ct);
        var dst = Owner.Repo.ReadCommitDetailsAsync(to, ct);
        var files = Owner.Repo.ReadChangedFilesAsync(from, to, ct);
        await Task.WhenAll(src, dst, files);
        ct.ThrowIfCancellationRequested();
        Apply(null, src.Result, dst.Result, files.Result, from);
    }

    /// <summary>Compact header: `Reverse Commits` swaps source and destination of a range.</summary>
    [RelayCommand]
    Task ReverseCommits()
    {
        if (Source is null || Destination is null) return Task.CompletedTask;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        var (from, to) = (Destination.Sha, Source.Sha);
        return Run();

        async Task Run()
        {
            try { await LoadRangeAsync(from, to, cts.Token); }
            catch (Exception e) when (e is OperationCanceledException || cts.IsCancellationRequested) { }
            catch (GitException e) { SetOverlay("Error", e.Message); }
        }
    }

    /// <summary>PARENTS: clicking a parent selects it in the commit list.</summary>
    [RelayCommand]
    void SelectParent(string sha) => Owner.CommitList.SelectCommit(sha, focus: true);

    void Apply(CommitDetails? commit, CommitDetails? source, CommitDetails? destination, IReadOnlyList<ChangedFile> files, string? from)
    {
        SetOverlay(null, null);
        if (commit is null) IsStash = false;
        _from = from;
        _to = commit?.Sha ?? destination?.Sha ?? "";
        Commit = commit;
        Source = source;
        Destination = destination;

        bool single = commit is not null;
        IsCommitTabEnabled = single;
        IsChangesTabEnabled = single || source is not null;
        if (source is not null) SelectedTab = ChangesTab; // two commits: only Changes

        // Keep the previously selected file if it still exists, else the first file.
        // No null step in between (the diff would flash empty); the list may push null while its items change.
        var keep = SelectedFile?.Path;
        _applying = true;
        Files = files;
        SelectedFile = files.FirstOrDefault(f => f.Path == keep) ?? files.FirstOrDefault();
        _applying = false;
        ShowSelectedDiff(); // explicit: an equal record for the new commit raises no change
    }

    void SetOverlay(string? title, string? text)
    {
        OverlayTitle = title;
        OverlayText = text;
    }

    partial void OnSelectedFileChanged(ChangedFile? value)
    {
        if (!_applying) ShowSelectedDiff();
    }

    void ShowSelectedDiff() =>
        Diff.Show(SelectedFile is not { } f || _to.Length == 0 ? null : new DiffRequest(_from, _to, f.Path, f.OldPath));
}

/// <summary>One row of the compact header.</summary>
public sealed record HeaderRow(CommitDetails Commit)
{
    public string Author => Commit.AuthorName;
    public string Email => Commit.AuthorEmail;
    public string Sha7 => Commit.Sha[..Math.Min(7, Commit.Sha.Length)];
    public string Date => Commit.AuthorDate.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    public string Subject => Commit.Subject;
    public bool IsMultiLine => Commit.Message.Contains('\n');
    public string ToolTip => Commit.Message.TrimEnd();
}

/// <summary>Binary = unknown-binary cards, Image = image diff.</summary>
public enum DiffState { None, Text, Binary, Image, TooLarge, Error }

/// <summary>Image diff radio bar.</summary>
public enum ImageMode { SideBySide, Swipe, OnionSkin }

/// <summary>Hunk and line staging (diff pane): which floating buttons the diff offers.</summary>
public enum LineActionSet { None, StageDiscard, Unstage }

/// <summary>The diff viewer of one file, unified ("Split") or side-by-side layout.</summary>
public sealed partial class DiffViewModel(GitRepository repo) : ObservableObject
{
    // Which view is shown for a file: limits for the too-large fallback (non-inline mode).
    public const int MaxTotalLength = 1_048_576;
    // Untracked files above 5 MiB: not diffed until Load Diff.
    public const long MaxUntrackedSize = 5_242_880;
    const string TooLargeText = "Changes are too large to display";

    CancellationTokenSource? _cts;
    DiffRequest? _request, _shown;
    bool _force, _invalidText, _running, _pending;
    LineActionSet _actions; // the set for the requested diff; LineActions takes it when that diff is shown

    /// <summary>The finished load of the last request (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsImage), nameof(IsCards), nameof(IsTooLarge), nameof(IsError), nameof(IsEmpty), nameof(ShowHeader),
        nameof(IsSideBySideText))]
    public partial DiffState State { get; private set; }

    /// <summary>Document of a text diff; the view shows <see cref="Text"/> and draws backgrounds from it.</summary>
    [ObservableProperty]
    public partial DiffDocument? Document { get; private set; }

    public string Text { get; private set; } = "";

    // Setting `views.diffLayout.<place>`: "unified" or "parallel" per place.
    public const string LocalChangesLayoutKey = "views.diffLayout.commit", HistoryLayoutKey = "views.diffLayout.history";
    // The Changes tab of the main window uses the `revision` key.
    public const string ChangesLayoutKey = "views.diffLayout.revision";

    /// <summary>Settings key of this place's layout; null = unified only, no layout button (Blame).</summary>
    public string? LayoutKey { get; init; }

    public bool ShowLayoutToggle => LayoutKey is not null;

    /// <summary>The layout button (checked = side by side). Read from the settings each time, so another view of the
    /// same place shows the stored layout with its next file. Limitation: views that are open do not switch at once.</summary>
    public bool SideBySide
    {
        get => LayoutKey is { } key && Settings.Get(key, "unified") == "parallel";
        set
        {
            // Compared with the shown layout too: another view of the place may have stored the value already.
            if (LayoutKey is not { } key || (value == SideBySide && value == (Sides is not null))) return;
            Settings.Set(key, value ? "parallel" : "unified");
            ShowLayout();
        }
    }

    public string LayoutToolTip => SideBySide ? "Split diff" : "Side by side diff";

    /// <summary>The two panes of the side-by-side layout; null in the unified layout. A new result sets it together with
    /// <see cref="Document"/> (the view reacts to that change); its own change event means the layout was switched.</summary>
    public SideBySideDocument? Sides { get; private set; }

    /// <summary>The left (old) pane is shown.</summary>
    public bool IsSideBySideText => State == DiffState.Text && Sides is not null;

    void ShowLayout()
    {
        OnPropertyChanged(nameof(SideBySide));
        OnPropertyChanged(nameof(LayoutToolTip));
        // Limitation: the layout button splits on the UI thread (one linear pass, the word diff is reused); a new diff is
        // split in the background with its document.
        Sides = SideBySide && Document is { } doc ? DiffDocumentBuilder.SideBySide(doc) : null;
        OnPropertyChanged(nameof(Sides));
        OnPropertyChanged(nameof(IsSideBySideText));
    }

    /// <summary>The parsed diff currently shown (null when not a text diff); line actions index into it.</summary>
    public FileDiff? FileDiff { get; private set; }

    /// <summary>Set by Local Changes; None = commit diffs (no buttons). None while a load is pending: the buttons and
    /// keys must never act on the previous file's diff with the next file's action set.</summary>
    [ObservableProperty] public partial LineActionSet LineActions { get; set; }

    /// <summary>Runs a line action: confirmation, apply and refresh are the caller's job.</summary>
    public Func<PatchAction, IReadOnlyList<HunkLine>, Task>? LineActionHandler { get; set; }

    /// <summary>The current document is a reload of the same working-tree file: the view keeps its scroll position.</summary>
    public bool KeepScroll { get; private set; }

    /// <summary>The reload produced the same lines as before (status refreshes re-show the same request): the view keeps
    /// its editor document, selection and hover; only the new Document/FileDiff objects are taken.</summary>
    public bool SameContent { get; private set; }

    [ObservableProperty] public partial string TooLargeTitle { get; private set; } = TooLargeText;

    [ObservableProperty]
    public partial string? ErrorText { get; private set; }

    // Header path: "<old dir><old name> → <dir><name>"; the old part only for renames.
    [ObservableProperty] public partial string OldDir { get; private set; } = "";
    [ObservableProperty] public partial string OldName { get; private set; } = "";
    [ObservableProperty] public partial string Arrow { get; private set; } = "";
    [ObservableProperty] public partial string Dir { get; private set; } = "";
    [ObservableProperty] public partial string Name { get; private set; } = "";

    /// <summary>Header centre: tooltip with the full path (shown only while the path is trimmed).</summary>
    [ObservableProperty] public partial string PathToolTip { get; private set; } = "";

    // Settings. Limitation: per tab and not saved.
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FewerLinesCommand), nameof(MoreLinesCommand))]
    public partial int Context { get; private set; } = 3;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FewerLinesCommand), nameof(MoreLinesCommand))]
    public partial bool EntireFile { get; set; }

    [ObservableProperty]
    public partial bool IgnoreWhitespace { get; set; }

    /// <summary>The header's `Show entire file` and context-size buttons; off where the view is always the entire file (Blame).</summary>
    public bool ShowContextControls { get; init; } = true;

    public bool IsText => State == DiffState.Text;
    public bool IsImage => State == DiffState.Image;
    public bool IsCards => State is DiffState.Binary or DiffState.Image;
    public bool IsTooLarge => State == DiffState.TooLarge;
    public bool IsError => State == DiffState.Error;
    public bool IsEmpty => State == DiffState.None;

    /// <summary>Header strip: hidden for fallback messages.</summary>
    public bool ShowHeader => State is DiffState.Text or DiffState.Binary or DiffState.Image;

    /// <summary>Which diffs have the buttons. A body line with U+FFFD is not valid UTF-8 and a text patch
    /// would corrupt it. Not built yet: byte-exact patches and LFS cards come later; until then those have no buttons.</summary>
    public bool ShowsLineActions =>
        LineActions != LineActionSet.None && State == DiffState.Text && FileDiff is { IsSubmodule: false } && !_invalidText;

    // ---- Binary and LFS views ----

    const string HighlightKey = "views.diff.imagePixelHighlight";
    Bitmap? _difference; // made with the cards (setting on) or in the background when Highlight pixels is turned on
    IReadOnlyList<BinaryCard>? _building; // the cards a background difference is being made for; SetCards leaves their bitmaps to it
    object? _imageFile;  // the file Mode, OnionOpacity and SwipePosition belong to (reset for every new file)

    /// <summary>Old card first; one card when the file was added or removed.</summary>
    [ObservableProperty] public partial IReadOnlyList<BinaryCard> Cards { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSideBySide), nameof(IsSwipe), nameof(IsOnionSkin), nameof(IsStacked))]
    public partial ImageMode Mode { get; set; }

    /// <summary>Onion Skin slider: opacity of the new image (1 = only new).</summary>
    [ObservableProperty] public partial double OnionOpacity { get; set; } = 1;

    /// <summary>Swipe divider within the images' common width (0..1); starts in the middle.</summary>
    [ObservableProperty] public partial double SwipePosition { get; set; } = 0.5;

    // Radio buttons: unchecking one (because another was checked) is ignored.
    public bool IsSideBySide { get => Mode == ImageMode.SideBySide; set { if (value) Mode = ImageMode.SideBySide; } }
    public bool IsSwipe { get => Mode == ImageMode.Swipe; set { if (value) Mode = ImageMode.Swipe; } }
    public bool IsOnionSkin { get => Mode == ImageMode.OnionSkin; set { if (value) Mode = ImageMode.OnionSkin; } }

    public Bitmap? OldImage => Cards.Count == 2 ? Cards[0].Image : null;
    public Bitmap? NewImage => Cards.Count == 2 ? Cards[1].Image : null;

    /// <summary>The radio bar: only when both sides decoded.</summary>
    public bool ShowModes => OldImage is not null && NewImage is not null;

    /// <summary>Swipe or Onion Skin replace the cards.</summary>
    public bool IsStacked => ShowModes && Mode != ImageMode.SideBySide;

    /// <summary>`Highlight pixels` is enabled only for identical pixel sizes; otherwise disabled and unchecked.</summary>
    public bool CanHighlight => OldImage is { } o && NewImage is { } n && o.PixelSize == n.PixelSize;

    /// <summary>Global setting. Limitation: other open diff
    /// views pick a change up with their next file.</summary>
    public bool HighlightPixels
    {
        get => CanHighlight && Settings.Get(HighlightKey, false);
        set
        {
            Settings.Set(HighlightKey, value);
            OnPropertyChanged();
            if (HighlightPixels && _difference is null && _building != Cards) Highlighting = BuildDifferenceAsync(Cards);
            ShowOverlay();
        }
    }

    /// <summary>The difference image being made after Highlight pixels was turned on (tests await it).</summary>
    internal Task Highlighting { get; private set; } = Task.CompletedTask;

    /// <summary>The difference layer while it is shown (drawn over the new image in every mode); null until it is made.</summary>
    public Bitmap? Overlay => HighlightPixels ? _difference : null;

    /// <summary>Makes the difference image off the UI thread; a result for cards that were replaced meanwhile is
    /// disposed together with those cards' bitmaps.</summary>
    async Task BuildDifferenceAsync(IReadOnlyList<BinaryCard> cards)
    {
        _building = cards;
        var difference = await Task.Run(() => BinaryCard.Difference(cards[0].Image!, cards[1].Image!));
        if (_building == cards) _building = null;
        if (cards != Cards)
        {
            DisposeImages(cards, difference);
            return;
        }
        _difference = difference;
        ShowOverlay();
    }

    /// <summary>Shows new cards and disposes the previous bitmaps. <paramref name="file"/> identifies the file: a refresh
    /// of the same file (Local Changes re-shows an equal request) keeps mode, opacity and divider.</summary>
    void SetCards(IReadOnlyList<BinaryCard> cards, object? file, Bitmap? difference = null)
    {
        var (oldCards, oldDifference) = (Cards, _difference);
        _difference = difference;
        Cards = cards;
        if (!Equals(file, _imageFile))
        {
            _imageFile = file;
            Mode = ImageMode.SideBySide;
            OnionOpacity = 1;
            SwipePosition = 0.5;
        }
        OnPropertyChanged(nameof(OldImage));
        OnPropertyChanged(nameof(NewImage));
        OnPropertyChanged(nameof(ShowModes));
        OnPropertyChanged(nameof(IsStacked));
        OnPropertyChanged(nameof(CanHighlight));
        OnPropertyChanged(nameof(HighlightPixels));
        ShowOverlay();
        DisposeImages(oldCards == _building ? [] : oldCards, oldDifference);
    }

    void ClearCards()
    {
        if (Cards.Count > 0) SetCards([], null);
    }

    static void DisposeImages(IEnumerable<BinaryCard> cards, Bitmap? difference)
    {
        foreach (var c in cards) c.Image?.Dispose();
        difference?.Dispose();
    }

    void ShowOverlay()
    {
        OnPropertyChanged(nameof(Overlay));
        if (Cards.Count == 2) Cards[1].Overlay = Overlay;
    }

    /// <summary>Both sides of a binary diff as cards; image files with their bytes and decoded bitmaps; with
    /// <paramref name="highlight"/> also the difference image when both sides have the same pixel size.</summary>
    async Task<(List<BinaryCard> Cards, Bitmap? Difference)> LoadCardsAsync(DiffRequest q, FileDiff d, bool highlight, CancellationToken ct)
    {
        bool image = ImageDiff.IsImagePath(q.Path);
        var oldSide = await repo.ReadBinarySideAsync(q, d, newSide: false, withBytes: image, ct);
        var newSide = await repo.ReadBinarySideAsync(q, d, newSide: true, withBytes: image, ct);
        var oldImage = BinaryCard.Decode(oldSide?.Bytes, q.OldPath ?? q.Path, out var oldNote);
        var newImage = BinaryCard.Decode(newSide?.Bytes, q.Path, out var newNote);
        bool both = oldSide is not null && newSide is not null;
        var cards = new List<BinaryCard>();
        if (oldSide is not null) cards.Add(new(both ? "old" : "removed", false, q.OldPath ?? q.Path, oldSide, oldImage, oldNote));
        if (newSide is not null) cards.Add(new(both ? "new" : "created", true, q.Path, newSide, newImage, newNote));
        var difference = highlight && oldImage is not null && newImage is not null && oldImage.PixelSize == newImage.PixelSize
            ? BinaryCard.Difference(oldImage, newImage) : null;
        return (cards, difference);
    }

    /// <summary>Runs the handler for the lines; ignored while a previous action runs or when nothing applies.</summary>
    public async Task RunLineActionAsync(PatchAction action, IReadOnlyList<HunkLine> lines)
    {
        if (_running || LineActionHandler is not { } run || lines.Count == 0 || !ShowsLineActions) return;
        _running = true;
        try { await run(action, lines); }
        finally { _running = false; }
    }

    partial void OnEntireFileChanged(bool value) => Reload();
    partial void OnIgnoreWhitespaceChanged(bool value) => Reload();

    [RelayCommand(CanExecute = nameof(CanFewerLines))]
    void FewerLines() { Context--; Reload(); }
    bool CanFewerLines() => !EntireFile && Context > 1;

    [RelayCommand(CanExecute = nameof(CanMoreLines))]
    void MoreLines() { Context++; Reload(); }
    bool CanMoreLines() => !EntireFile;

    /// <summary>`Load Diff` on the too-large fallback: show it anyway.</summary>
    [RelayCommand]
    void LoadDiff() { _force = true; Reload(); }

    void Reload() => Show(_request, keepForce: true);

    /// <summary>Shows the diff of one file; null clears the viewer. A newer call cancels a running load.
    /// <paramref name="actions"/>: the line actions of that diff (default: keep the current set).</summary>
    public void Show(DiffRequest? request, bool keepForce = false, LineActionSet? actions = null)
    {
        _cts?.Cancel();
        _actions = actions ?? (_pending ? _actions : LineActions);
        _pending = true;
        LineActions = LineActionSet.None;
        if (!keepForce && request != _request) _force = false; // a refresh re-shows an equal request: "Load Diff" sticks
        _request = request;
        SetPath(request);
        if (request is null)
        {
            SetText(null, "", null, null, false);
            ClearCards();
            State = DiffState.None;
            Loading = Task.CompletedTask;
            return;
        }
        var cts = _cts = new CancellationTokenSource();
        Loading = LoadAsync(request with { Context = Context, EntireFile = EntireFile, IgnoreWhitespace = IgnoreWhitespace }, _force, cts.Token);
    }

    async Task LoadAsync(DiffRequest q, bool force, CancellationToken ct)
    {
        try
        {
            if (q.Working == WorkingDiff.Untracked && !force && !ImageDiff.IsImagePath(q.Path) &&
                new FileInfo(Path.Combine(repo.Info.WorkTree, q.Path)) is { Exists: true, Length: > MaxUntrackedSize } big)
            {
                SetText(null, "", null, null, false);
                ClearCards();
                TooLargeTitle = $"Changes are too large ({FormatSize(big.Length)})";
                State = DiffState.TooLarge;
                return;
            }
            // Off the UI thread: Core does not use ConfigureAwait(false), so parsing would run on the caller's context.
            var diff = await Task.Run(() => repo.ReadFileDiffAsync(q, ct), ct);
            ct.ThrowIfCancellationRequested(); // a newer Show() must win over this result
            // Not built yet: a text diff of a Git LFS pointer should show the LFS card here.
            if (!diff.IsBinary && !force && (diff.TotalLength > MaxTotalLength || diff.IsMinified))
            {
                SetText(null, "", null, null, false);
                ClearCards();
                TooLargeTitle = TooLargeText;
                State = DiffState.TooLarge;
                return;
            }
            if (diff.IsBinary)
            {
                bool highlight = Settings.Get(HighlightKey, false);
                var (cards, difference) = await Task.Run(() => LoadCardsAsync(q, diff, highlight, ct), ct);
                if (ct.IsCancellationRequested)
                {
                    DisposeImages(cards, difference);
                    return;
                }
                SetText(null, "", null, null, false);
                SetCards(cards, (q.Path, q.OldPath, q.Working, q.FromSha, q.ToSha), difference);
                State = ImageDiff.IsImagePath(q.Path) ? DiffState.Image : DiffState.Binary;
                return;
            }
            // Large diffs: build the document and its text off the UI thread.
            bool split = SideBySide; // the settings are read here; the split is made with the document
            var (doc, text, sides, invalid) = await Task.Run(() =>
            {
                var d = DiffDocumentBuilder.Build(diff, q.EntireFile);
                return (d, d.Text, split ? DiffDocumentBuilder.SideBySide(d) : null,
                    diff.Hunks.Any(h => h.Lines.Any(l => l.Text.Contains('\uFFFD'))));
            }, ct);
            ct.ThrowIfCancellationRequested();
            SetText(doc, text, diff, q, invalid, sides);
            ClearCards();
            State = DiffState.Text;
        }
        catch (Exception e) when (e is OperationCanceledException || ct.IsCancellationRequested) { }
        catch (Exception e) when (e is GitException or IOException or UnauthorizedAccessException) // working-tree file reads
        {
            SetText(null, "", null, null, false);
            ClearCards();
            ErrorText = e.Message;
            State = DiffState.Error;
        }
    }

    /// <summary><paramref name="sides"/>: the side-by-side pair made with <paramref name="doc"/> off the UI thread.</summary>
    void SetText(DiffDocument? doc, string text, FileDiff? diff, DiffRequest? shown, bool invalidText, SideBySideDocument? sides = null)
    {
        KeepScroll = shown is { Working: not WorkingDiff.None } && _shown is { } s
            && s.Path == shown.Path && s.Working == shown.Working && s.EntireFile == shown.EntireFile;
        SameContent = KeepScroll && text == Text && doc is not null && Document is { } old
            && doc.Lines.Select(Key).SequenceEqual(old.Lines.Select(Key));
        _shown = shown;
        FileDiff = diff;
        _invalidText = invalidText;
        Text = text;
        // Both panes before the Document change the view reacts to, in the layout as stored now (another view of the
        // place may have switched it).
        OnPropertyChanged(nameof(SideBySide));
        OnPropertyChanged(nameof(LayoutToolTip));
        // Made here only when the layout was switched while the diff loaded.
        Sides = !SideBySide || doc is null ? null : sides ?? DiffDocumentBuilder.SideBySide(doc);
        Document = doc;
        OnPropertyChanged(nameof(IsSideBySideText));
        _pending = false;
        LineActions = _actions; // together with the FileDiff it applies to

        static (DocLineKind, string, int?, int?) Key(DocLine l) => (l.Kind, l.Text, l.OldNumber, l.NewNumber);
    }

    // Size format of the untracked too-large title: binary units, one decimal ("6.0 MB").
    static string FormatSize(long bytes) => (bytes / 1048576.0).ToString("0.0 MB", CultureInfo.InvariantCulture);

    void SetPath(DiffRequest? q)
    {
        (Dir, Name) = Split(q?.Path ?? "");
        bool renamed = q?.OldPath is { } old && old != q.Path;
        (OldDir, OldName) = renamed ? Split(q!.OldPath!) : ("", "");
        Arrow = renamed ? " → " : "";
        PathToolTip = OldDir + OldName + Arrow + Dir + Name;

        static (string, string) Split(string p) => (p[..(p.LastIndexOf('/') + 1)], p[(p.LastIndexOf('/') + 1)..]);
    }

    /// <summary>
    /// Navigating changes: a navigation group is a run of changed lines plus up to 2 context lines before it;
    /// runs with fewer than 4 lines between them are merged. Returns the first document line (0-based) of each group.
    /// Alignment lines count as changed, so a side-by-side pane finds the groups the other pane holds.
    /// </summary>
    public static List<int> NavigationTargets(DiffDocument? doc)
    {
        var targets = new List<int>();
        if (doc is null) return targets;
        int lastChanged = 0;
        for (int i = 0; i < doc.Lines.Count; i++)
        {
            if (!IsChange(doc.Lines[i].Kind)) continue;
            if (targets.Count == 0 || i - lastChanged - 1 >= 4)
            {
                int start = i;
                while (start > 0 && i - start < 2 && doc.Lines[start - 1].Kind == DocLineKind.Context) start--;
                targets.Add(start);
            }
            lastChanged = i;
        }
        return targets;
    }

    /// <summary>
    /// The group under the mouse: changed lines with at most 3 context lines between them form one group,
    /// plus up to 2 context lines before and after; never across hunk boundaries or before the end of the previous
    /// group. Returns inclusive document line ranges.
    /// </summary>
    public static List<(int First, int Last)> StagingGroups(DiffDocument doc)
    {
        var groups = new List<(int, int)>();
        var lines = doc.Lines;
        int start = -1, lastChanged = -1, floor = 0, hunk = -1; // floor: first line the next group may start at
        for (int i = 0; i <= lines.Count; i++)
        {
            var l = i < lines.Count ? lines[i] : null;
            // A hunk ends at a header, at the end, or (entire-file mode has no headers) where the hunk index changes.
            bool newHunk = l is { Hunk: >= 0 } && l.Hunk != hunk;
            bool boundary = l is null || l.Kind == DocLineKind.HunkHeader || newHunk;
            bool changed = l is not null && IsChange(l.Kind); // a side-by-side alignment line is changed
            if (start >= 0 && (boundary || (changed && ContextBetween(lastChanged, i) > 3))) Close(i);
            if (boundary) floor = l?.Kind == DocLineKind.HunkHeader ? i + 1 : i;
            if (newHunk) hunk = l!.Hunk;
            if (!changed) continue;
            if (start < 0)
            {
                start = i;
                while (start > floor && i - start < 2 && lines[start - 1].Kind == DocLineKind.Context) start--;
            }
            lastChanged = i;
        }
        return groups;

        int ContextBetween(int a, int b) => Enumerable.Range(a + 1, b - a - 1).Count(k => lines[k].Kind == DocLineKind.Context);

        // Trailing part: up to 2 context lines (and a "No newline" service line) before the limit.
        void Close(int limit)
        {
            int end = lastChanged, context = 0;
            while (end + 1 < limit && context < 2 && lines[end + 1].Kind is DocLineKind.Context or DocLineKind.Service)
                if (lines[++end].Kind == DocLineKind.Context) context++;
            groups.Add((start, end));
            floor = end + 1;
            start = -1;
        }
    }

    static bool IsChange(DocLineKind kind) => kind is DocLineKind.Added or DocLineKind.Removed or DocLineKind.Alignment;

    /// <summary>The added/removed lines among document lines first..last, as hunk lines of the shown diff.</summary>
    public static List<HunkLine> ChangedLines(DiffDocument doc, int first, int last) =>
        doc.Lines.Skip(first).Take(last - first + 1)
            .Where(l => l.Kind is DocLineKind.Added or DocLineKind.Removed && l.Hunk >= 0)
            .Select(l => new HunkLine(l.Hunk, l.Line)).ToList();
}
