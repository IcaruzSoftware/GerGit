using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;
using GerGit.Core.Conflicts;
using GerGit.Core.Git;

namespace GerGit.ViewModels;

/// <summary>Result of an edit in the result pane. Anything but Applied: the pane is
/// rebuilt from the model.</summary>
public enum ResultEdit { Applied, Protected, Mismatch }

/// <summary>
/// The three-pane merge-conflict resolver . Drives A's <see cref="MergeDocument"/>; <see cref="Layout"/> is rebuilt after every change and
/// <see cref="LayoutChanged"/> tells the view to refresh its panes. Shown by <see cref="Views.MergeResolverWindow"/>.
/// </summary>
public sealed partial class MergeResolverViewModel : ObservableObject
{
    /// <summary>Merge layout orientation (false = horizontal, the default).</summary>
    public const string VerticalKey = "merge.verticalLayout";

    readonly RepoTabViewModel _tab;
    MergeSide? _binaryChoice; // binary mode: the checked header box

    public MergeResolverViewModel(RepoTabViewModel tab, string path)
    {
        _tab = tab;
        Path = path;
        int slash = path.LastIndexOf('/');
        Dir = path[..(slash + 1)];
        Name = path[(slash + 1)..];
        Vertical = Settings.Get(VerticalKey, false);
    }

    public string Path { get; }
    public string Dir { get; }
    public string Name { get; }

    public RepoState State { get; private set; } = RepoState.Clean;
    public MergeDocument? Document { get; private set; }
    public MergeLayout Layout { get; private set; } = new([], [], []);

    /// <summary>The panes must be refilled from <see cref="Layout"/>.</summary>
    public event Action? LayoutChanged;

    /// <summary>The system beep of a protected edit; replaceable in tests.
    /// Limitation: the terminal bell only; a desktop sound needs a shared beep helper.</summary>
    public Action Beep { get; set; } = () => Console.Beep();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsBinary), nameof(CanResolve), nameof(ShowResolvedLabel), nameof(ShowCounter))]
    public partial bool IsLoaded { get; private set; }

    /// <summary>Text mode: the three editors, pill, prev/next and the orientation toggle.</summary>
    public bool IsText => IsLoaded && Document is { Mode: MergeMode.Text };

    /// <summary>Binary, LFS or submodule: both versions side by side and the two boxes as an exclusive choice.</summary>
    public bool IsBinary => IsLoaded && Document is { Mode: not MergeMode.Text };

    /// <summary>Counter pill and prev/next: text mode with a parsed document (a failed parse shows nothing, not a
    /// green 0/0).</summary>
    public bool ShowCounter => IsText && !Document!.Failed;

    /// <summary>Not built yet: a text placeholder instead of the image / binary / submodule viewers of the diff view.</summary>
    public string BinaryText => Document?.Mode switch
    {
        MergeMode.Submodule => "Submodule",
        MergeMode.Lfs => "LFS object",
        _ => "Binary file",
    };

    // ---------------------------------------------------------------- headers

    public string TheirsName => State.TheirsName;
    public string OursName => State.OursName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TheirsDate))]
    public partial CommitDetails? TheirsCommit { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OursDate))]
    public partial CommitDetails? OursCommit { get; private set; }

    public string TheirsDate => FormatDate(TheirsCommit);
    public string OursDate => FormatDate(OursCommit);

    static string FormatDate(CommitDetails? c) =>
        c?.AuthorDate.ToLocalTime().ToString("d MMM yyyy HH:mm", CultureInfo.InvariantCulture) ?? "";

    public bool TheirsChecked => IsBinary ? _binaryChoice == MergeSide.Theirs : Document is { TheirsChecked: true };
    public bool OursChecked => IsBinary ? _binaryChoice == MergeSide.Ours : Document is { OursChecked: true };

    // ---------------------------------------------------------------- counter, resolve state

    public int ResolvedCount => Document?.ResolvedCount ?? 0;
    public int ConflictCount => Document?.ConflictCount ?? 0;
    public string Counter => $"{ResolvedCount}/{ConflictCount}";
    public bool Complete => ResolvedCount == ConflictCount;
    public string CounterTip => Complete ? "All conflicts resolved" : $"Resolved {ResolvedCount} conflicts of {ConflictCount}";

    /// <summary>Text mode = every conflict has a result line; binary mode = one box checked.</summary>
    public bool CanResolve => IsText ? !Document!.Failed && Document.AllResolved : IsBinary && _binaryChoice is not null;

    // ---------------------------------------------------------------- orientation

    /// <summary>Vertical = three columns; horizontal (default) = theirs/ours above, result below. Saved on toggle.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OrientationIcon), nameof(ShowResolvedLabel))]
    public partial bool Vertical { get; set; }

    /// <summary>The `Resolved` label above the middle column: vertical layout only.</summary>
    public bool ShowResolvedLabel => IsText && Vertical;

    partial void OnVerticalChanged(bool value) => Settings.Set(VerticalKey, value);

    /// <summary>`columns-3` while pressing switches to vertical, `rows-2` while it switches back.</summary>
    public string OrientationIcon => Vertical ? "rows-2" : "columns-3";

    // ---------------------------------------------------------------- loading

    /// <summary>Reads the conflict state (side names), the merge document and the header commits. A git error is shown
    /// in the error window and closes the resolver.</summary>
    public async Task LoadAsync()
    {
        try
        {
            State = await _tab.Repo.ReadRepoStateAsync(_tab.Status);
            Document = await _tab.Repo.LoadMergeDocumentAsync(Path);
        }
        catch (GitException e)
        {
            Close(false);
            await _tab.ShowError("Git Error", e.Message);
            return;
        }
        OnPropertyChanged(nameof(TheirsName));
        OnPropertyChanged(nameof(OursName));
        OnPropertyChanged(nameof(BinaryText));
        IsLoaded = true;
        Changed();
        // Details for ours, and for theirs only in rebase state (the commit being applied).
        OursCommit = await DetailsAsync(State.OursSha);
        if (State.Kind == RepoStateKind.Rebase) TheirsCommit = await DetailsAsync(State.ActiveSha);
    }

    async Task<CommitDetails?> DetailsAsync(string? sha)
    {
        if (sha is null) return null;
        try { return await _tab.Repo.ReadCommitDetailsAsync(sha); }
        catch (GitException e)
        {
            Trace.WriteLine(e.Message); // the header shows the chip only
            return null;
        }
    }

    /// <summary>Rebuilds the layout and every derived value after a model change.</summary>
    void Changed()
    {
        Layout = Document is { Mode: MergeMode.Text } d ? MergeLayout.Build(d) : new([], [], []);
        foreach (var name in (string[])[nameof(TheirsChecked), nameof(OursChecked), nameof(ResolvedCount), nameof(ConflictCount),
                     nameof(Counter), nameof(Complete), nameof(CounterTip), nameof(CanResolve)])
            OnPropertyChanged(name);
        LayoutChanged?.Invoke();
    }

    // ---------------------------------------------------------------- selection

    /// <summary>Gutter tag click on a side-pane row.</summary>
    public void ToggleLine(MergeRow row)
    {
        if (Document is not { } d || row.Line is not { Kind: MergeLineKind.Conflict } line) return;
        d.ToggleLine(d.Chunks[row.Chunk], line);
        Changed();
    }

    /// <summary>Text of the floating chunk button of <paramref name="side"/>'s pane (theirs = Left, ours = Right).</summary>
    public string ChunkButtonText(int chunk, MergeSide side) =>
        (Document is { } d && MergeDocument.IsSideSelected(d.Chunks[chunk], side) ? "Remove " : "Select ")
        + (side == MergeSide.Theirs ? "Left" : "Right");

    public void ToggleChunkSide(int chunk, MergeSide side)
    {
        if (Document is not { } d) return;
        d.ToggleChunkSide(d.Chunks[chunk], side);
        Changed();
    }

    /// <summary>Header box click. Text mode: check = select that side in every conflict, uncheck = deselect it (the other
    /// side stays). Binary mode: an exclusive choice; clicking the checked box unchecks it.</summary>
    public void ToggleAll(MergeSide side)
    {
        if (!IsLoaded || Document is not { } d) return;
        if (IsBinary) _binaryChoice = _binaryChoice == side ? null : side;
        else d.SetSide(side, side == MergeSide.Theirs ? !d.TheirsChecked : !d.OursChecked);
        Changed();
    }

    [RelayCommand]
    void ToggleTheirs() => ToggleAll(MergeSide.Theirs);

    [RelayCommand]
    void ToggleOurs() => ToggleAll(MergeSide.Ours);

    // ---------------------------------------------------------------- editing

    /// <summary>
    /// Applies one editor change of the result pane (offset in the pane text, rows joined by "\n"). Edits inside result
    /// lines change those lines; line breaks split a line into new lines of the same chunk (Source null); joined lines
    /// are removed. Touching a filler, `--- Merge Conflict ---` or selected-placeholder row beeps and changes nothing.
    /// Mismatch = the removed text is not what the model has there.
    /// </summary>
    public ResultEdit EditResult(int offset, string removed, string inserted)
    {
        var rows = Layout.Result;
        int first = -1, last = -1, firstStart = 0, start = 0;
        for (int i = 0; i < rows.Count && last < 0; i++)
        {
            int end = start + rows[i].Text.Length;
            if (first < 0 && offset <= end) (first, firstStart) = (i, start);
            if (first >= 0 && offset + removed.Length <= end) last = i;
            start = end + 1;
        }
        if (Document is not { } d || first < 0 || last < 0) return ResultEdit.Mismatch;

        var span = rows.Skip(first).Take(last - first + 1).ToList();
        var combined = string.Join("\n", span.Select(r => r.Text));
        int local = offset - firstStart;
        if (string.CompareOrdinal(combined, local, removed, 0, removed.Length) != 0) return ResultEdit.Mismatch;
        if (span.Any(r => r.Kind != MergeRowKind.Line))
        {
            Beep();
            return ResultEdit.Protected;
        }

        var parts = (combined[..local] + inserted.ReplaceLineEndings("\n") + combined[(local + removed.Length)..]).Split('\n');
        var lines = span.Select(r => (Line: r.Result!, Chunk: d.Chunks[r.Chunk])).ToList();
        for (int i = 0; i < Math.Min(parts.Length, lines.Count); i++) lines[i].Line.Edit(parts[i]);
        for (int i = parts.Length; i < lines.Count; i++) lines[i].Chunk.Result.Remove(lines[i].Line); // joined lines
        if (parts.Length > lines.Count)
        {
            var (anchor, chunk) = lines[^1];
            var ending = anchor.Text[anchor.Body.Length..];
            int at = chunk.Result.IndexOf(anchor) + 1;
            foreach (var part in parts[lines.Count..])
                chunk.Result.Insert(at++, new ResultLine(null, part + (ending.Length > 0 ? ending : "\n")));
        }
        Changed();
        return ResultEdit.Applied;
    }

    // ---------------------------------------------------------------- navigation

    /// <summary>(first row, last row) of every conflict chunk in pane rows (the same in all three panes).</summary>
    public IReadOnlyList<(int First, int Last)> ConflictRows()
    {
        var list = new List<(int, int)>();
        var rows = Layout.Ours;
        for (int i = 0; i < rows.Count; i++)
        {
            var kind = Document!.Chunks[rows[i].Chunk].Kind;
            if (kind != MergeChunkKind.Conflict || (i > 0 && rows[i - 1].Chunk == rows[i].Chunk)) continue;
            int j = i;
            while (j + 1 < rows.Count && rows[j + 1].Chunk == rows[i].Chunk) j++;
            list.Add((i, j));
        }
        return list;
    }

    /// <summary>Next: the first conflict starting after the middle row; Previous: the last one ending before it.
    /// No wrap-around; null = no move. Design choice: Previous skips a conflict that already starts at or
    /// below the top row, else it would scroll to the conflict just shown and never move.</summary>
    public int? TargetRow(int topRow, int middleRow, bool next) => next
        ? ConflictRows().Select(c => (int?)c.First).FirstOrDefault(f => f > middleRow)
        : ConflictRows().Where(c => c.Last < middleRow && c.First < topRow).Select(c => (int?)c.First).LastOrDefault();

    // ---------------------------------------------------------------- closing

    /// <summary>Null while open; true = Resolve, false = Cancel.</summary>
    public bool? Result { get; private set; }

    public event Action? CloseRequested;

    /// <summary>The resolve job started by <see cref="ResolveAsync"/>; true = it succeeded.</summary>
    public Task<bool> Resolving { get; private set; } = Task.FromResult(false);

    public void Cancel() => Close(false);

    void Close(bool result)
    {
        if (Result is not null) return;
        Result = result;
        CloseRequested?.Invoke();
    }

    /// <summary>
    /// Resolve: closes the window, then writes the result and stages it (text) or takes the chosen side (binary) as a
    /// job of the tab, which refreshes and shows a git error in the error window.
    /// </summary>
    public Task<bool> ResolveAsync()
    {
        if (!CanResolve || Result is not null) return Resolving;
        var doc = Document!;
        var text = doc.Mode == MergeMode.Text ? doc.BuildResult() : null;
        bool ours = _binaryChoice == MergeSide.Ours;
        var state = State;
        Close(true);
        return Resolving = RunAsync();

        async Task<bool> RunAsync()
        {
            var job = await _tab.RunJobAsync("Resolve conflict", async j => text is not null
                ? await _tab.Repo.WriteResolvedAsync(j, Path, text)
                : await _tab.Repo.ResolveWithSideAsync(j, state, [Path], ours));
            return job.Outcome == JobOutcome.Succeeded;
        }
    }
}
