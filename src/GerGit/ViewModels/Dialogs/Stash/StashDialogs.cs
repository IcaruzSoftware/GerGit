using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.ViewModels.Dialogs;

// "Stash" and "Commit operations" dialogs. Each dialog stays open while its job runs and is opened through StashOps / CommitOps.

/// <summary>"Save Stash" and "Save Snapshot" (same fields, same remembered "Stage new files").</summary>
public sealed partial class SaveStashViewModel : DialogViewModel
{
    internal const string StageNewFilesKey = "lastUsed.stash.addNewFilesFirst";
    readonly RepoTabViewModel _tab;

    public SaveStashViewModel(RepoTabViewModel tab, bool snapshot)
    {
        _tab = tab;
        IsSnapshot = snapshot;
        Title = snapshot ? "Save snapshot" : "Save stash";
        Description = snapshot
            ? "Save your local changes to a new stash, but keep them in the working directory"
            : "Save your local changes to a new stash";
        SubmitText = snapshot ? "Save Snapshot" : "Save Stash";
        Width = 590;
        MinHeight = 215;
        StageNewFiles = Settings.Get(StageNewFilesKey, false);
    }

    public bool IsSnapshot { get; }

    [ObservableProperty] public partial string Message { get; set; } = "";
    [ObservableProperty] public partial bool StageNewFiles { get; set; }

    protected override Task OnSubmitAsync()
    {
        Settings.Set(StageNewFilesKey, StageNewFiles);
        SetStatus(DialogStatus.InProgress, "Stashing...");
        return _tab.RunJobAsync(IsSnapshot ? "Stash snapshot" : "Stash local changes", async job =>
        {
            if (StageNewFiles) await _tab.Repo.StageUntrackedAsync(job);
            if (!IsSnapshot) return await _tab.Repo.SaveStashAsync(job, Message);
            var source = _tab.ActiveBranchName ?? _tab.Refs.HeadSha?[..7] ?? "";
            var message = string.IsNullOrWhiteSpace(Message) ? $"Snapshot on '{source}' {GitRepository.StashTimestamp()}" : Message;
            return await _tab.Repo.SaveSnapshotAsync(job, message);
        }, dialog: this);
    }
}

/// <summary>A checkable file row of the Stash Files dialog.</summary>
public sealed partial class StashFileRow(string path, string icon) : ObservableObject
{
    public string Path { get; } = path;
    public string Icon { get; } = icon;
    [ObservableProperty] public partial bool IsChecked { get; set; }
}

/// <summary>"Stash Files (partial stash)" / "Save stash (partial stash)".</summary>
public sealed partial class StashFilesViewModel : DialogViewModel
{
    static readonly StringComparer Natural =
        StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
    readonly RepoTabViewModel _tab;

    public StashFilesViewModel(RepoTabViewModel tab, IReadOnlyList<string> preselected)
    {
        _tab = tab;
        Title = "Save stash";
        Description = "Save your local modifications to a new stash. BOTH staged and unstaged changes will be stashed";
        SubmitText = "Save Stash";
        Width = 590;
        Height = 370;
        CanResize = true;
        var selected = preselected.ToHashSet(StringComparer.Ordinal);
        Files = tab.Status.Unstaged.Concat(tab.Status.Staged)
            .Where(e => e.Kind != ChangeKind.Ignored)
            .DistinctBy(e => e.Path)
            .OrderBy(e => e.Path, Natural)
            .Select(e => new StashFileRow(e.Path, e.IsSubmodule ? "package" : "file") { IsChecked = selected.Contains(e.Path) })
            .ToList();
        foreach (var f in Files) f.PropertyChanged += (_, _) => CanSubmit = Files.Any(r => r.IsChecked);
        CanSubmit = Files.Any(r => r.IsChecked);
    }

    public IReadOnlyList<StashFileRow> Files { get; }

    [ObservableProperty] public partial string Message { get; set; } = "";

    protected override Task OnSubmitAsync()
    {
        var paths = Files.Where(f => f.IsChecked).Select(f => f.Path).ToHashSet(StringComparer.Ordinal);
        var entries = _tab.Status.Unstaged.Concat(_tab.Status.Staged).Where(e => paths.Contains(e.Path)).ToList();
        SetStatus(DialogStatus.InProgress, "Stashing...");
        return _tab.RunJobAsync("Partial stash", async job => await _tab.Repo.StashFilesAsync(job, Message, entries), dialog: this,
            afterRefresh: () => { _tab.IsLocalChangesView = true; return Task.CompletedTask; });
    }
}

/// <summary>Apply Stash; "Delete stash after applying" = pop (git keeps the stash on a conflict).</summary>
public sealed partial class ApplyStashViewModel : DialogViewModel
{
    internal const string DropKey = "lastUsed.stash.dropAfterApply";
    readonly RepoTabViewModel _tab;

    public ApplyStashViewModel(RepoTabViewModel tab, StashEntry stash)
    {
        _tab = tab;
        Stash = stash;
        Title = "Apply Stash";
        Description = "Apply changes of the stash to your working directory";
        SubmitText = "Apply";
        Width = 540;
        MinHeight = 195;
        DeleteAfterApply = Settings.Get(DropKey, false);
    }

    public StashEntry Stash { get; }

    [ObservableProperty] public partial bool DeleteAfterApply { get; set; }

    protected override Task OnSubmitAsync()
    {
        Settings.Set(DropKey, DeleteAfterApply);
        SetStatus(DialogStatus.InProgress, "Applying stash...");
        return _tab.RunJobAsync($"Apply stash '{Stash.Name}'",
            async job => (await _tab.Repo.ApplyStashAsync(job, Stash.Name, Stash.Sha, DeleteAfterApply)).Result, dialog: this,
            afterRefresh: () =>
            {
                if (_tab.Refs.HeadSha is { } head) _tab.CommitList.SelectCommit(head);
                return Task.CompletedTask;
            });
    }
}

/// <summary>Rename Stash.</summary>
public sealed partial class RenameStashViewModel : DialogViewModel
{
    readonly RepoTabViewModel _tab;

    public RenameStashViewModel(RepoTabViewModel tab, StashEntry stash)
    {
        _tab = tab;
        Stash = stash;
        Title = "Rename Stash";
        Description = "Update stash message";
        SubmitText = "Rename";
        Width = 500;
        Message = stash.ShortSubject;
    }

    public StashEntry Stash { get; }

    [ObservableProperty] public partial string Message { get; set; } = "";

    partial void OnMessageChanged(string value) =>
        CanSubmit = !string.IsNullOrWhiteSpace(value) && value != Stash.ShortSubject;

    protected override Task OnSubmitAsync()
    {
        string? newSha = null;
        SetStatus(DialogStatus.InProgress, "Updating message...");
        return _tab.RunJobAsync($"Rename stash '{Stash.ShortSubject}'", async job =>
        {
            (var r, newSha) = await _tab.Repo.RenameStashAsync(job, Stash, Message);
            return r;
        }, dialog: this, afterRefresh: () =>
        {
            if (newSha is not null) _tab.CommitList.SelectCommit(newSha);
            return Task.CompletedTask;
        });
    }
}

/// <summary>Delete Stash(es).</summary>
public sealed class DeleteStashViewModel : DialogViewModel
{
    readonly RepoTabViewModel _tab;

    public DeleteStashViewModel(RepoTabViewModel tab, IReadOnlyList<StashEntry> stashes)
    {
        _tab = tab;
        Stashes = stashes;
        bool many = stashes.Count > 1;
        Title = many ? "Delete Stashes" : "Delete Stash";
        Description = many ? "Delete stashes from your repository" : "Delete stash from your repository";
        SubmitText = many ? $"Delete {stashes.Count} stashes" : "Delete";
        Width = 520;
    }

    public IReadOnlyList<StashEntry> Stashes { get; }
    public bool IsMultiple => Stashes.Count > 1;
    public string Label => IsMultiple ? "Stashes:" : "Stash:";

    protected override Task OnSubmitAsync()
    {
        SetStatus(DialogStatus.InProgress, "Deleting...");
        return _tab.RunJobAsync(IsMultiple ? $"Delete {Stashes.Count} stashes" : $"Delete '{Stashes[0].Name}'",
            job => _tab.Repo.DropStashesAsync(job, Stashes), dialog: this,
            afterRefresh: () => { _tab.CommitList.SelectCommit(Stashes[0].ParentSha); return Task.CompletedTask; });
    }
}

/// <summary>A commit row of the cherry-pick / revert dialog.</summary>
public sealed record CommitItem(string Sha, string Subject)
{
    public string ShortSha => Sha.Length > 7 ? Sha[..7] : Sha;
}

/// <summary>"Cherry-pick" and "Revert" (same form; revert has one commit and no "Append origin").
/// <see cref="Commits"/> in list order (newest first); git gets them oldest first.</summary>
public sealed partial class CommitPickViewModel : DialogViewModel
{
    internal const string RecordOriginKey = "lastUsed.cherryPick.recordOrigin";
    readonly RepoTabViewModel _tab;
    int _previewVersion;

    public CommitPickViewModel(RepoTabViewModel tab, IReadOnlyList<CommitItem> commits, IReadOnlyList<CommitItem> parents, bool revert)
    {
        _tab = tab;
        Commits = commits;
        Parents = parents;
        IsRevert = revert;
        Title = revert ? "Revert" : "Cherry Pick";
        Description = revert ? "Revert changes of the individual commit" : "Apply changes of the individual commit";
        SubmitText = revert ? "Revert" : commits.Count == 1 ? "Cherry Pick" : $"Cherry Pick {commits.Count} commits";
        Width = revert ? 590 : 620;
        MinHeight = 180;
        AppendOrigin = !revert && Settings.Get(RecordOriginKey, false);
        if (parents.Count > 0) SelectedParent = parents[0]; // starts the preview
        else Preview = RunPreviewAsync();
    }

    public IReadOnlyList<CommitItem> Commits { get; }
    public IReadOnlyList<CommitItem> Parents { get; }
    public bool IsRevert { get; }
    public bool IsMultiple => Commits.Count > 1;
    public bool IsSingle => !IsMultiple;
    public bool HasParents => Parents.Count > 0;
    public bool ShowAppendOrigin => !IsRevert;
    public string CommitsLabel => IsMultiple ? "Commits to apply:" : "Commit to apply:";

    /// <summary>The running conflict preview (started on open and on every parent change).</summary>
    internal Task Preview { get; private set; } = Task.CompletedTask;

    [ObservableProperty] public partial CommitItem? SelectedParent { get; set; }

    [ObservableProperty] public partial bool CommitChanges { get; set; } = true;

    [ObservableProperty] public partial bool AppendOrigin { get; set; }

    int ParentIndex => SelectedParent is { } p ? Parents.ToList().IndexOf(p) + 1 : 1;

    partial void OnSelectedParentChanged(CommitItem? value)
    {
        CanSubmit = !HasParents || value is not null;
        Preview = RunPreviewAsync();
    }

    partial void OnCommitChangesChanged(bool value)
    {
        if (!value) AppendOrigin = false; // "-x" needs a commit
    }

    async Task RunPreviewAsync()
    {
        int version = ++_previewVersion;
        SetStatus(DialogStatus.None);
        if (HasParents && SelectedParent is null) return;
        var ok = IsRevert
            ? await _tab.Repo.PreviewRevertAsync(Commits[0].Sha, ParentIndex)
            : await _tab.Repo.PreviewCherryPickAsync(Commits.Reverse().Select(c => c.Sha).ToList(), ParentIndex);
        if (version != _previewVersion || IsBusy || Result is not null) return;
        var what = IsRevert ? "Revert" : "Cherry pick";
        if (ok is true) SetStatus(DialogStatus.Success, $"{what} can be done without conflicts");
        else if (ok is false) SetStatus(DialogStatus.Warning, $"{what} will cause conflicts");
    }

    protected override Task OnSubmitAsync()
    {
        _previewVersion++; // a late preview must not overwrite the progress text
        int? mainline = HasParents ? ParentIndex : null;
        var target = Commits[0].Sha; // the newest by selection (cherry-pick) / the reverted commit
        if (IsRevert)
        {
            SetStatus(DialogStatus.InProgress, "Reverting...");
            return _tab.RunJobAsync($"Revert '{Commits[0].ShortSha}'",
                async job => await _tab.Repo.RevertAsync(job, target, mainline, CommitChanges), dialog: this,
                afterRefresh: () => { _tab.CommitList.SelectCommit(target); return Task.CompletedTask; });
        }
        if (CommitChanges) Settings.Set(RecordOriginKey, AppendOrigin);
        SetStatus(DialogStatus.InProgress, "Cherry-picking...");
        var shas = Commits.Reverse().Select(c => c.Sha).ToList();
        return _tab.RunJobAsync("Cherry-pick",
            async job => await _tab.Repo.CherryPickAsync(job, shas, mainline, CommitChanges, CommitChanges && AppendOrigin), dialog: this,
            afterRefresh: () => { _tab.CommitList.SelectCommit(target); return Task.CompletedTask; });
    }
}
