using CommunityToolkit.Mvvm.ComponentModel;

namespace GerGit.ViewModels.Dialogs.Refs;

/// <summary>Rebase dialog: source and target rows, the dependent-branches box and list, autostash.</summary>
public sealed partial class RebaseViewModel : RefDialogViewModel
{
    public const string UpdateRefsKey = "lastUsed.rebasePlain.updateRefs", AutostashKey = "lastUsed.rebasePlain.autoStash";

    public RebaseViewModel(RefPoint source, RefPoint target)
    {
        Source = source;
        Target = target;
        Title = "Rebase";
        Description = "Copy commits from one branch to another";
        SubmitText = "Rebase";
        Width = 570;
        UpdateRefs = Settings.Get(UpdateRefsKey, false);
        Autostash = Settings.Get(AutostashKey, false);
    }

    public RefPoint Source { get; }
    public RefPoint Target { get; }

    /// <summary>The prediction started when the dialog opened (tests await it).</summary>
    public Task Preview { get; internal set; } = Task.CompletedTask;

    /// <summary>Other local branches pointing into the rebased range (short names); set by the preview.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdateRefs), nameof(ShowBranchList))]
    public partial IReadOnlyList<string> Branches { get; set; } = [];

    public bool ShowUpdateRefs => Branches.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowBranchList))]
    public partial bool UpdateRefs { get; set; }

    public bool ShowBranchList => ShowUpdateRefs && UpdateRefs;

    [ObservableProperty]
    public partial bool Autostash { get; set; }

    /// <summary>`--update-refs` only when the branch list was shown and the box is checked.</summary>
    public bool EffectiveUpdateRefs => ShowUpdateRefs && UpdateRefs;
}
