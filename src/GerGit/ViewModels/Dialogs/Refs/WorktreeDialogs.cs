using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;

namespace GerGit.ViewModels.Dialogs.Refs;

/// <summary>
/// Create Worktree (<see cref="Starts"/> set: start drop-down + branch name box), "Checkout Branch as Worktree"
/// and "Checkout Remote Branch as Worktree" (<see cref="Branch"/> set: a read-only branch row). All three have the
/// Location row: the text box shows &lt;container&gt;/&lt;name with "/" → "-"&gt;; the browse button sets the container.
/// </summary>
public sealed partial class CreateWorktreeViewModel : RefDialogViewModel
{
    readonly IReadOnlySet<string> _branches;
    readonly Func<string, bool> _hasWorktree;

    /// <param name="hasWorktree">True when a worktree already has refs/heads/&lt;name&gt; checked out.</param>
    public CreateWorktreeViewModel(string container, IReadOnlyList<RefPoint>? starts, RefPoint? start, RefPoint? branch,
        string branchLabel, string name, IReadOnlySet<string> localBranches, Func<string, bool> hasWorktree)
    {
        (_branches, _hasWorktree) = (localBranches, hasWorktree);
        Starts = starts ?? [];
        Branch = branch;
        BranchLabel = branchLabel;
        Width = 590;
        SubmitText = "Create";
        Start = start;
        Name = name;
        Container = container;
    }

    public bool IsCreate => Branch is null;
    public bool IsCheckout => Branch is not null;
    public IReadOnlyList<RefPoint> Starts { get; }

    /// <summary>The fixed branch of the two checkout dialogs.</summary>
    public RefPoint? Branch { get; }
    public string BranchLabel { get; }

    [ObservableProperty]
    public partial RefPoint? Start { get; set; }

    [ObservableProperty]
    public partial string Name { get; set; } = "";

    /// <summary>The worktrees container folder.</summary>
    [ObservableProperty]
    public partial string Container { get; set; } = "";

    [ObservableProperty]
    public partial string Location { get; set; } = "";

    partial void OnStartChanged(RefPoint? value) => Validate();
    partial void OnNameChanged(string value)
    {
        Location = System.IO.Path.Combine(Container, value.Replace('/', '-'));
        Validate(); // "a/b" and "a-b" give the same Location: no OnLocationChanged then
    }
    partial void OnContainerChanged(string value) => Location = System.IO.Path.Combine(value, Name.Replace('/', '-'));
    partial void OnLocationChanged(string value) => Validate();

    void Validate()
    {
        if (IsCreate)
        {
            var (ok, msg) = RefName.Validate(Name);
            if (!ok) { SetValid(false, msg); return; }
        }
        // Create and the remote dialog warn; for a local branch the menu offers the dialog only when it has no
        // worktree, so the same warning there is just a fallback.
        if (_hasWorktree(Name)) { SetValid(false, $"Worktree '{Name}' already exists"); return; }
        if (IsCreate && _branches.Contains(Name)) { SetValid(false, $"Branch '{Name}' already exists"); return; }
        SetValid(Location.Trim().Length > 0 && (IsCheckout || Start is not null));
    }
}

/// <summary>Delete Worktree: a confirmation without fields (no body view).</summary>
public sealed class DeleteWorktreeViewModel : RefDialogViewModel;
