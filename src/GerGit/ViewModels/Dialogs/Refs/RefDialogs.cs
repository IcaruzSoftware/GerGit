using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit.ViewModels.Dialogs.Refs;

/// <summary>A read-only ref row of the dialogs (RefView): Arg = what git gets (full ref or full sha), Sha = its commit.</summary>
public sealed record RefPoint(string Icon, string Text, string? Detail, string Arg, string Sha);

/// <summary>A labelled ref row ("Switch to:", "Fast-Forward to:", ...).</summary>
public sealed record LabeledPoint(string Label, RefPoint Point);

/// <summary>A ref dialog whose Submit runs the operation <see cref="RefOps"/> set in <see cref="Run"/>.</summary>
public abstract class RefDialogViewModel : DialogViewModel
{
    internal Func<Task>? Run { get; set; }

    protected override Task OnSubmitAsync() => Run is { } run ? run() : base.OnSubmitAsync();
}

/// <summary>
/// Local-changes block: shown only when the working tree is dirty (and the host wants it, e.g. Create Branch
/// with "Check out after create"). Shift held relabels "Stash and reapply" to "Leave as stash".
/// </summary>
public sealed partial class LocalChangesBlockViewModel : ObservableObject
{
    /// <summary>Settings key of the remembered choice (stash and reapply or not); shared by every dialog with this block.</summary>
    public const string RememberKey = "lastUsed.checkout.autoStash";

    public LocalChangesBlockViewModel(bool dirty)
    {
        IsDirty = dirty;
        Choice = Settings.Get(RememberKey, false) ? LocalChangesMode.StashAndReapply : LocalChangesMode.DontChange;
    }

    public bool IsDirty { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial bool Applies { get; set; } = true;

    public bool IsVisible => IsDirty && Applies;

    /// <summary>DontChange, StashAndReapply or Discard (the radio group).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDontChange), nameof(IsStash), nameof(IsDiscard))]
    public partial LocalChangesMode Choice { get; set; }

    public bool IsDontChange { get => Choice == LocalChangesMode.DontChange; set { if (value) Choice = LocalChangesMode.DontChange; } }
    public bool IsStash { get => Choice == LocalChangesMode.StashAndReapply; set { if (value) Choice = LocalChangesMode.StashAndReapply; } }
    public bool IsDiscard { get => Choice == LocalChangesMode.Discard; set { if (value) Choice = LocalChangesMode.Discard; } }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StashLabel))]
    public partial bool ShiftHeld { get; set; }

    public string StashLabel => ShiftHeld ? "Leave as stash" : "Stash and reapply";

    /// <summary>The effective mode: a hidden block means a plain checkout.</summary>
    public LocalChangesMode Mode =>
        !IsVisible ? LocalChangesMode.DontChange
        : Choice == LocalChangesMode.StashAndReapply && ShiftHeld ? LocalChangesMode.LeaveAsStash
        : Choice;

    /// <summary>On submit: only "Stash and reapply" without Shift is remembered as "stash".</summary>
    public void Remember()
    {
        if (IsVisible) Settings.Set(RememberKey, Mode == LocalChangesMode.StashAndReapply);
    }
}

/// <summary>Ref-name validation shared by the name boxes: rules, then uniqueness (case-insensitive).</summary>
static class NameRules
{
    public static (bool Valid, string? Message) Check(string name, IReadOnlySet<string> existing, string kind)
    {
        var (ok, msg) = RefName.Validate(name);
        if (ok && existing.Contains(name)) return (false, $"{kind} '{name}' already exists");
        return (ok, msg);
    }
}

/// <summary>Create Branch.</summary>
public sealed partial class CreateBranchViewModel : RefDialogViewModel
{
    public const string CheckoutKey = "lastUsed.branch.checkoutAfterCreate";
    readonly IReadOnlySet<string> _branches;

    public CreateBranchViewModel(RefPoint start, string name, IReadOnlySet<string> localBranches, bool dirty)
    {
        _branches = localBranches;
        Start = start;
        Local = new LocalChangesBlockViewModel(dirty);
        Title = "Create Branch";
        Description = "Use '/' as a path separator to create folders";
        Width = 600;
        CheckoutAfterCreate = Settings.Get(CheckoutKey, false);
        OnCheckoutAfterCreateChanged(CheckoutAfterCreate);
        Name = name;
        OnNameChanged(name);
    }

    public RefPoint Start { get; }
    public LocalChangesBlockViewModel Local { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial bool CheckoutAfterCreate { get; set; }

    partial void OnNameChanged(string value)
    {
        var (ok, msg) = NameRules.Check(value, _branches, "Branch");
        SetValid(ok, msg);
    }

    partial void OnCheckoutAfterCreateChanged(bool value)
    {
        SubmitText = value ? "Create and Checkout" : "Create";
        Local.Applies = value;
    }
}

/// <summary>"Checkout Branch", "Checkout and Fast-Forward" and "Checkout Commit": ref rows + local changes.</summary>
public sealed class CheckoutViewModel(IReadOnlyList<LabeledPoint> rows, bool dirty) : RefDialogViewModel
{
    public IReadOnlyList<LabeledPoint> Rows { get; } = rows;
    public LocalChangesBlockViewModel Local { get; } = new(dirty);
}

/// <summary>Track remote branch dialog.</summary>
public sealed partial class TrackRemoteBranchViewModel : RefDialogViewModel
{
    readonly IReadOnlySet<string> _branches;

    public TrackRemoteBranchViewModel(RefPoint remote, string name, IReadOnlySet<string> localBranches, bool dirty)
    {
        _branches = localBranches;
        Remote = remote;
        Local = new LocalChangesBlockViewModel(dirty);
        Title = "Track Remote Branch";
        Description = "Create new local branch which tracks remote branch";
        SubmitText = "Track";
        Width = 540;
        MinHeight = 190;
        Name = name;
        OnNameChanged(name);
    }

    public RefPoint Remote { get; }
    public LocalChangesBlockViewModel Local { get; }

    [ObservableProperty]
    public partial string Name { get; set; }

    partial void OnNameChanged(string value)
    {
        var (ok, msg) = NameRules.Check(value, _branches, "Branch");
        SetValid(ok, msg);
    }
}

public enum SyncAction { Checkout, Rebase, Merge, Reset }

/// <summary>Checkout and sync dialog. The Action drop-down has separators at index 1 and 4.</summary>
public sealed partial class CheckoutSyncViewModel : RefDialogViewModel
{
    public const string StashKey = "lastUsed.checkoutSync.autoStash";
    static readonly SyncAction?[] Items = [SyncAction.Checkout, null, SyncAction.Rebase, SyncAction.Merge, null, SyncAction.Reset];

    public CheckoutSyncViewModel(RefPoint local, string remoteName)
    {
        Local = local;
        Title = "Checkout Branch";
        Description = $"Switch to '{local.Text}' branch";
        Width = 590;
        CheckoutText = $"Checkout '{local.Text}'";
        RebaseText = $"Rebase '{local.Text}' onto '{remoteName}'";
        MergeText = $"Merge '{remoteName}' into '{local.Text}'";
        ResetText = $"Reset (--hard) '{local.Text}' to '{remoteName}'";
        SubmitText = "Checkout";
    }

    public RefPoint Local { get; }
    public string CheckoutText { get; }
    public string RebaseText { get; }
    public string MergeText { get; }
    public string ResetText { get; }

    [ObservableProperty]
    public partial int SelectedIndex { get; set; }

    /// <summary>"Stash and reapply local changes": initially unchecked; the value is saved but never read back.</summary>
    [ObservableProperty]
    public partial bool Stash { get; set; }

    public SyncAction Action
    {
        get => Items[SelectedIndex] ?? SyncAction.Checkout;
        set => SelectedIndex = Array.IndexOf(Items, value);
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (value < 0 || value >= Items.Length || Items[value] is null) { SelectedIndex = 0; return; }
        SubmitText = Action switch
        {
            SyncAction.Rebase => "Checkout and Rebase",
            SyncAction.Merge => "Checkout and Merge",
            SyncAction.Reset => "Checkout and Reset",
            _ => "Checkout",
        };
    }
}

/// <summary>Rename Branch. RemoteBranches = existing remote branch short names ("origin/x").</summary>
public sealed partial class RenameBranchViewModel : RefDialogViewModel
{
    readonly IReadOnlySet<string> _branches, _remoteBranches;

    /// <param name="remote">The upstream's remote when the upstream has the same short name as the branch, else null
    /// (no "Also rename" checkbox).</param>
    public RenameBranchViewModel(string oldName, string? remote, string newName, IReadOnlySet<string> localBranches,
        IReadOnlySet<string> remoteBranches)
    {
        (OldName, Remote, _branches, _remoteBranches) = (oldName, remote, localBranches, remoteBranches);
        Title = "Rename Local Branch";
        Description = "Rename local branch";
        SubmitText = "Rename";
        Width = 500;
        MinHeight = 166;
        // Literal underscores: the check box text treats "_" as the access-key marker.
        RenameRemoteText = remote is null ? "" : $"Also rename {remote}/{oldName}".Replace("_", "__");
        NewName = newName;
        Validate();
    }

    public string OldName { get; }
    public string? Remote { get; }
    public bool ShowRenameRemote => Remote is not null;
    public string RenameRemoteText { get; }

    [ObservableProperty]
    public partial string NewName { get; set; }

    [ObservableProperty]
    public partial bool RenameRemote { get; set; }

    partial void OnNewNameChanged(string value) => Validate();
    partial void OnRenameRemoteChanged(bool value) => Validate();

    /// <summary>Validation order 1-5.</summary>
    void Validate()
    {
        var name = NewName;
        if (name.Length == 0) { SetValid(false); return; }
        string? warning = null;
        if (RenameRemote && Remote is not null)
        {
            if (_remoteBranches.Contains($"{Remote}/{name}")) { SetValid(false, $"Branch {Remote}/{name} already exists"); return; }
            warning = "Renaming can break tracking references for other users";
        }
        if (string.Equals(name, OldName, StringComparison.OrdinalIgnoreCase)) { SetValid(false, warning); return; }
        var (ok, msg) = NameRules.Check(name, _branches, "Branch");
        SetValid(ok, msg ?? warning);
    }
}

/// <summary>One row of a delete dialog. RemoteBranch = the corresponding (same-name) remote branch "origin/foo" shown
/// after "→" while the remote option is checked.</summary>
public sealed record DeleteRow(string Icon, string Name, string? RemoteBranch);

/// <summary>"Delete Branch(es)", "Delete Remote Branch(es)", "Delete Tag(s)": rows plus one optional check box.</summary>
public sealed partial class DeleteRefsViewModel(IReadOnlyList<DeleteRow> rows, string label) : RefDialogViewModel
{
    public IReadOnlyList<DeleteRow> Rows { get; } = rows;
    public bool IsSingle => Rows.Count == 1;
    public bool IsMultiple => Rows.Count > 1;
    public DeleteRow First => Rows[0];
    public string Label { get; } = label;

    /// <summary>Null hides the check box. "_" marks the access key.</summary>
    public string? CheckText { get; init; }
    public bool HasCheck => CheckText is not null;

    /// <summary>"Alt+R" where the text has an access key (the remote-branch option), else no tooltip.</summary>
    public string? CheckToolTip => CheckText?.Contains('_') == true ? "Alt+R" : null;
    public bool CheckEnabled { get; init; } = true;

    /// <summary>The corresponding same-name remote branch shown after the single-branch check box (remote icon + name).</summary>
    public string? CheckDetail { get; init; }

    /// <summary>A warning icon left of the check box while it is checked (the destructive remote option).</summary>
    public bool CheckWarns { get; init; } = true;

    /// <summary>Height of the multi-row list (80 px for remote branches and tags; local branches fill the
    /// resizable window).</summary>
    public double ListHeight { get; init; } = double.NaN;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowCheckWarning))]
    public partial bool IsChecked { get; set; }

    public bool ShowCheckWarning => IsChecked && CheckWarns;

    /// <summary>Folder name of the other worktree that has the (single) branch checked out; null hides "Also delete
    /// worktree".</summary>
    public string? WorktreeName { get; init; }
    public bool HasWorktreeCheck => WorktreeName is not null;

    [ObservableProperty]
    public partial bool DeleteWorktree { get; set; }
}

public enum ResetKind { Soft, Mixed, Hard }

/// <summary>Reset Branch.</summary>
public sealed partial class ResetViewModel(string branch, RefPoint target) : RefDialogViewModel
{
    public string Branch { get; } = branch;
    public RefPoint Target { get; } = target;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind))]
    public partial int SelectedIndex { get; set; } = (int)ResetKind.Mixed;

    public ResetKind Kind
    {
        get => (ResetKind)SelectedIndex;
        set => SelectedIndex = (int)value;
    }
}

public enum MergeKind { Default, NoFastForward, Squash, NoCommit }

/// <summary>Merge Branch. The Merge Option drop-down has a separator at index 2.</summary>
public sealed partial class MergeViewModel : RefDialogViewModel
{
    static readonly MergeKind?[] Items = [MergeKind.Default, MergeKind.NoFastForward, null, MergeKind.Squash, MergeKind.NoCommit];

    public MergeViewModel(RefPoint source, RefPoint destination)
    {
        Source = source;
        Destination = destination;
        Title = "Merge Branch";
        Description = "Merge branch into another one";
        SubmitText = "Merge";
        Width = 560;
        MinHeight = 225;
        MinWidth = 560;
        CanResize = true; // the checks panel grows the dialog; long node ids want a wider one
        Panel = Checks;
        Kind = MergeKind.NoFastForward; // always, not remembered
    }

    /// <summary>The checks panel below the buttons (shown once a hook ran).</summary>
    public ChecksViewModel Checks { get; } = new();

    /// <summary>A Squash / Don't Commit merge kept open: the commit subject gets the focus once the dialog closes.</summary>
    internal bool FocusSubjectOnClose { get; set; }

    public RefPoint Source { get; }
    public RefPoint Destination { get; }

    /// <summary>The conflict preview started when the dialog opened (tests await it).</summary>
    public Task Preview { get; internal set; } = Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Kind))]
    public partial int SelectedIndex { get; set; }

    public MergeKind Kind
    {
        get => Items[SelectedIndex] ?? MergeKind.Default;
        set => SelectedIndex = Array.IndexOf(Items, value);
    }

    partial void OnSelectedIndexChanged(int value)
    {
        if (value < 0 || value >= Items.Length || Items[value] is null) SelectedIndex = 0;
    }
}

/// <summary>Create Tag.</summary>
public sealed partial class CreateTagViewModel : RefDialogViewModel
{
    public const string PushKey = "lastUsed.tag.pushAfterCreate";
    readonly IReadOnlySet<string> _tags;

    public CreateTagViewModel(RefPoint start, IReadOnlySet<string> tags, int remoteCount)
    {
        _tags = tags;
        Start = start;
        Title = "Create Tag";
        Description = "Create annotated tag at the selected point";
        Width = 590;
        MinHeight = 290;
        // With zero remotes the box still says "Push" (the push loop does nothing).
        PushText = remoteCount > 1 ? "Push to all remotes" : "Push";
        Push = Settings.Get(PushKey, false);
        OnPushChanged(Push);
        OnNameChanged(Name);
    }

    public RefPoint Start { get; }
    public string PushText { get; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    [ObservableProperty]
    public partial string Message { get; set; } = "";

    [ObservableProperty]
    public partial bool Push { get; set; }

    partial void OnNameChanged(string value)
    {
        var (ok, msg) = NameRules.Check(value, _tags, "Tag");
        SetValid(ok, msg);
    }

    partial void OnPushChanged(bool value) => SubmitText = value ? "Create and Push" : "Create";
}

/// <summary>Tag Details window.</summary>
public sealed class TagDetailsViewModel : DialogViewModel
{
    public TagDetailsViewModel(string tagName, TagDetails details)
    {
        TagName = tagName;
        TaggerName = details.TaggerName;
        TaggerEmail = details.TaggerEmail;
        Date = details.Date?.ToString("d MMM yyyy HH:mm:ss zzz", System.Globalization.CultureInfo.InvariantCulture) ?? "";
        Message = details.Message;
        Title = "Tag Details";
        SubmitText = null;
        CancelText = "Close";
        Width = 650;
        Height = 350;
        CanResize = true;
    }

    public string TagName { get; }
    public string TaggerName { get; }
    public string TaggerEmail { get; }
    public string Date { get; }
    public string Message { get; }
}
