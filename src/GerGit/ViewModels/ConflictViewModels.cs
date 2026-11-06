using System.Diagnostics;
using System.Globalization;
using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;

namespace GerGit.ViewModels;

// ---- Conflict banner ----

/// <summary>A piece of the banner text: plain text, or a ref link (Sha != null) that selects its commit.</summary>
public sealed record BannerPiece(string Text, string? Sha = null)
{
    public bool IsLink => Sha is not null;
}

/// <summary>What the banner shows. Progress variant (rebase, am): Left, a progress bar (Value of Maximum), Right.</summary>
public sealed record BannerContent(IReadOnlyList<BannerPiece> Left, IReadOnlyList<BannerPiece> Right, int Value = 0, int Maximum = 0)
{
    public bool HasProgress => Maximum > 0;
    public bool HasRight => Right.Count > 0;

    /// <summary>13 px in the progress variant, else 14 px.</summary>
    public double FontSize => HasProgress ? 13 : 14;

    public string Text => string.Concat(Left.Concat(Right).Select(p => p.Text));

    /// <summary>The banner of <paramref name="s"/>; null = hidden (clean; bisect has no banner until bisect exists).</summary>
    public static BannerContent? For(RepoState s)
    {
        int n = s.Unmerged.Count;
        string fix = $"Fix {n} {(n == 1 ? "conflict" : "conflicts")}";
        BannerPiece T(string text) => new(text);
        BannerPiece L(string name, string? sha) => new(name, sha);
        string Abbrev(string? sha) => sha is null ? "" : sha[..Math.Min(7, sha.Length)];
        string done = n > 0 ? $"'. {fix} and then continue." : "'. All conflicts fixed.";

        switch (s.Kind)
        {
            case RepoStateKind.Merge:
                return new([T("Merging branch '"), L(s.TheirsName, s.TheirsSha), T("' into '"), L(s.OursName, s.OursSha), T(done)], []);
            case RepoStateKind.Rebase:
            {
                List<BannerPiece> right = [];
                // The link is left out when the active commit is unknown (unverified: wording without it).
                if (n > 0)
                    right.AddRange(s.ActiveSha is { } a ? [T($"{fix} with '"), L(Abbrev(a), a), T("' and then continue.")] : [T($"{fix} and then continue.")]);
                if (s.AmendSha is { } am) right.Add(T((n > 0 ? " " : "") + $"Amending '{Abbrev(am)}'."));
                return new([T("Rebasing '"), L(s.TheirsName, s.TheirsSha), T("' → '"), L(s.OursName, s.OursSha), T($"' (rebased {s.Done}/{s.Total} commits)")],
                    right, s.Done + 1, Math.Max(1, s.Total));
            }
            case RepoStateKind.CherryPick:
                return new([T("Cherry-picking commit '"), L(Abbrev(s.TheirsSha), s.TheirsSha), T(done)], []);
            case RepoStateKind.CherryPickSequence:
                return new([T("Cherry-pick in progress")], []);
            case RepoStateKind.Revert:
                return new([T("Reverting commit '"), L(Abbrev(s.TheirsSha), s.TheirsSha), T(done)], []);
            case RepoStateKind.Squash:
                return new([T(n > 0 ? $"Squashing commits. {fix} and continue." : "Squashing commits")], []);
            case RepoStateKind.ApplyPatches:
                return new([T($"Applying a series of patches (AM) in progress (applied {s.Done}/{s.Total} commits)")],
                    n > 0 ? [T($"{fix} and then continue.")] : [], s.Done + 1, Math.Max(1, s.Total));
            case RepoStateKind.Unmerged:
                return new([T($"Working directory contains {n} unmerged {(n == 1 ? "file" : "files")}.")], []);
            default:
                return null; // clean, bisect (the bisect banner is not built yet)
        }
    }

    /// <summary>Abort confirmation: title, text, confirm button; null = no confirmation (bisect).</summary>
    public static (string Title, string Text, string Button)? AbortTexts(RepoStateKind kind)
    {
        const string lost = "All uncommitted changes will be lost.";
        return kind switch
        {
            RepoStateKind.Merge or RepoStateKind.Squash => ("Do you want to abort the merge?", lost, "Abort merge"),
            RepoStateKind.Rebase => ("Do you want to abort the rebase?", "Undo the rebase and check out the original branch?", "Abort rebase"),
            RepoStateKind.CherryPick or RepoStateKind.CherryPickSequence => ("Do you want to abort cherry-pick?", lost, "Abort cherry-pick"),
            RepoStateKind.Revert => ("Do you want to abort revert?", lost, "Abort revert"),
            RepoStateKind.ApplyPatches => ("Do you want to abort applying patches?", lost, "Abort AM"),
            RepoStateKind.Unmerged => ("Do you want to abort the merge?", lost, "Abort"),
            _ => null,
        };
    }
}

// ---- Conflict card ----

/// <summary>One commit row of a side card: avatar, abbreviated SHA button, subject, date.</summary>
public sealed class ConflictCommitRow(CommitHeader header)
{
    public string Sha => header.Sha;
    public string ShortSha => header.Sha[..Math.Min(7, header.Sha.Length)];
    public string Subject => header.Subject;
    public string Date => header.AuthorDate.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture);
    public string AuthorName => header.AuthorName;
    public string AuthorEmail => header.AuthorEmail;
    public string ToolTip => $"{header.AuthorName} <{header.AuthorEmail}>";
}

/// <summary>One side card: change-type icon + word, ref chip, the commits of that side that touched the file.</summary>
public sealed partial class ConflictSide(string name, char letter, bool theirs) : ObservableObject
{
    public string Name { get; } = name;
    public string Word { get; } = UnmergedFile.SideWord(letter);
    public string Icon { get; } = letter switch { 'A' => "file-plus", 'D' => "file-minus", _ => "file-pen" };

    /// <summary>The theirs card (left) has its check box on the inner (right) side.</summary>
    public Dock CheckDock { get; } = theirs ? Dock.Right : Dock.Left;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCommits))]
    public partial IReadOnlyList<ConflictCommitRow> Commits { get; set; } = [];

    public bool HasCommits => Commits.Count > 0;
}

/// <summary>
/// The conflict card shown instead of the diff when an unmerged Unstaged row is selected. Unresolved: the two
/// side cards (theirs left, ours right), the primary button and the external merge button; resolved: the "File
/// conflicts are resolved." strip.
/// </summary>
public sealed partial class ConflictCardViewModel : ObservableObject
{
    readonly LocalChangesViewModel _owner;

    public ConflictCardViewModel(LocalChangesViewModel owner, RepoState state, UnmergedFile file)
    {
        _owner = owner;
        State = state;
        File = file;
        Theirs = new ConflictSide(state.TheirsName, file.Theirs, true);
        Ours = new ConflictSide(state.OursName, file.Ours, false);
        TheirsChecked = OursChecked = file.MergeAllowed; // both checked when merging is possible
        CommitsLoading = LoadCommitsAsync();
    }

    public RepoState State { get; }
    public UnmergedFile File { get; }
    public string Path => File.Path;
    public ConflictSide Theirs { get; }
    public ConflictSide Ours { get; }

    /// <summary>The background load of the side commit lists (tests await it).</summary>
    public Task CommitsLoading { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText), nameof(PrimaryEnabled), nameof(ShowMergeTool), nameof(ShowMergeToolArrow))]
    public partial bool TheirsChecked { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText), nameof(PrimaryEnabled), nameof(ShowMergeTool), nameof(ShowMergeToolArrow))]
    public partial bool OursChecked { get; set; }

    /// <summary>The visible external merge tools when the card was made.</summary>
    public IReadOnlyList<ExternalTool> MergeTools { get; } = ExternalTools.Visible(ToolKind.Merge);

    /// <summary>`Merge in &lt;Tool name&gt;`: the primary tool.</summary>
    public string MergeToolText => ExternalTools.Primary(MergeTools) is { } t ? $"Merge in {t.Name}" : "";

    /// <summary>"Conflict card" controls: both cards checked, merge allowed, at least one tool.</summary>
    public bool ShowMergeTool => TheirsChecked && OursChecked && File.MergeAllowed && MergeTools.Count > 0;

    /// <summary>The drop-down arrow: more than one tool.</summary>
    public bool ShowMergeToolArrow => ShowMergeTool && MergeTools.Count > 1;

    /// <summary>The drop-down menu: every visible tool by name.</summary>
    public IReadOnlyList<MenuEntry> MergeToolMenu() =>
        MergeTools.Select(t => Menus.Item(t.Name, () => ExternalTools.MergeAsync(_owner.Owner, t, Path))).ToList();

    [RelayCommand]
    Task MergeInTool() =>
        ShowMergeTool && ExternalTools.Primary(MergeTools) is { } t ? ExternalTools.MergeAsync(_owner.Owner, t, Path) : Task.CompletedTask;

    /// <summary>No conflict markers left; read on every status refresh.</summary>
    [ObservableProperty] public partial bool IsResolved { get; set; }

    /// <summary>Primary button text and action by checkbox state.</summary>
    public string PrimaryText => (TheirsChecked, OursChecked) switch
    {
        (true, true) => File.MergeAllowed ? "Merge" : "Select version to resolve with",
        (false, true) => File.OursDeleted ? "Delete file" : $"Choose {State.OursName}",
        (true, false) => File.TheirsDeleted ? "Delete file" : $"Choose {State.TheirsName}",
        _ => "Merge",
    };

    public bool PrimaryEnabled => TheirsChecked != OursChecked || (TheirsChecked && File.MergeAllowed);

    /// <summary>Merge = the resolver window; one side = resolve every selected unmerged file with that side.</summary>
    [RelayCommand]
    Task Primary()
    {
        if (!PrimaryEnabled) return Task.CompletedTask;
        return TheirsChecked && OursChecked ? _owner.Owner.ShowResolver(Path) : _owner.ResolveSelectedAsync(OursChecked);
    }

    /// <summary>A SHA button in a side list: the commit list, that commit selected.
    /// Limitation: the file is not selected in the details pane.</summary>
    [RelayCommand]
    void OpenCommit(string sha) => _owner.Owner.OpenCommit(sha);

    async Task LoadCommitsAsync()
    {
        try
        {
            var commits = await _owner.Owner.Repo.ReadConflictCommitsAsync(State, Path);
            Theirs.Commits = commits.Theirs.Select(h => new ConflictCommitRow(h)).ToList();
            Ours.Commits = commits.Ours.Select(h => new ConflictCommitRow(h)).ToList();
        }
        catch (Exception e) { Trace.WriteLine(e); } // a side without commits hides its list
    }

    /// <summary>Same file and same sides: the card (and its check boxes) is kept over a refresh.</summary>
    internal bool Matches(RepoState state, UnmergedFile file) => File == file && State with { Unmerged = state.Unmerged } == state;
}
