using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Hosting;

namespace GerGit.ViewModels.Dialogs.Remote;

// The dialogs of "Controls". Job dialogs close at once and then run their job (the returned task ends with the
// job, so tests can await SubmitAsync); Add/Edit remote and Change tracking stay open while their job runs.

/// <summary>Fetch dialog.</summary>
public sealed partial class FetchViewModel : DialogViewModel
{
    readonly RemoteOps _ops;

    /// <summary>Host icon per remote name for the remote drop-down (view root: `c:RefView.HostOf`).</summary>
    public Func<string, HostType> HostOf => _ops.HostOf;

    public FetchViewModel(RemoteOps ops, string? remote)
    {
        _ops = ops;
        Title = "Fetch";
        Description = "Fetch latest changes from remote repository";
        SubmitText = "Fetch";
        MinHeight = 180;
        Remotes = ops.RemoteNames;
        AllRemotes = Settings.Get(RemoteOps.AllRemotesKey, false);
        SelectedRemote = ops.PickRemote(remote);
        CanSubmit = SelectedRemote is not null;
    }

    public IReadOnlyList<string> Remotes { get; }

    /// <summary>Submit needs a selected remote even with "Fetch all remotes" checked.</summary>
    [ObservableProperty]
    public partial string? SelectedRemote { get; set; }

    partial void OnSelectedRemoteChanged(string? value) => CanSubmit = value is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemoteEnabled))]
    public partial bool AllRemotes { get; set; }

    public bool RemoteEnabled => !AllRemotes;

    protected override Task OnSubmitAsync()
    {
        Settings.Set(RemoteOps.AllRemotesKey, AllRemotes);
        Close(true);
        return _ops.FetchAsync(AllRemotes ? null : SelectedRemote);
    }
}

/// <summary>Pull dialog: shows the cached data, reloads in the background and only then allows Pull.</summary>
public sealed partial class PullViewModel : DialogViewModel
{
    readonly RemoteOps _ops;

    /// <summary>Host icon per remote name for the remote drop-down (view root: `c:RefView.HostOf`).</summary>
    public Func<string, HostType> HostOf => _ops.HostOf;
    readonly string? _preselected;
    string? _activeAtOpen;
    bool _loaded, _building;

    public PullViewModel(RemoteOps ops, string? remoteBranch)
    {
        _ops = ops;
        _preselected = remoteBranch;
        Title = "Pull";
        Description = "Pull remote branches and merge them into your local branch";
        SubmitText = "Pull";
        Width = 520;
        Rebase = Settings.Get(RemoteOps.RebaseKey, false);
        AutoStash = Settings.Get(RemoteOps.AutoStashKey, false);
        Build();
        Loading = LoadAsync();
    }

    /// <summary>The reload that runs when the dialog opens; Pull is disabled until it ends.</summary>
    public Task Loading { get; }

    [ObservableProperty]
    public partial IReadOnlyList<string> Remotes { get; private set; } = [];

    [ObservableProperty]
    public partial string? SelectedRemote { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<GitRef> Branches { get; private set; } = [];

    [ObservableProperty]
    public partial GitRef? SelectedBranch { get; set; }

    /// <summary>The active local branch, null when HEAD is detached.</summary>
    [ObservableProperty]
    public partial string? Into { get; private set; }

    [ObservableProperty]
    public partial bool Rebase { get; set; }

    [ObservableProperty]
    public partial bool AutoStash { get; set; }

    async Task LoadAsync()
    {
        await _ops.Tab.RefreshAsync();
        await _ops.Tab.Refreshing;
        _loaded = true;
        // Rebuilt only when the active branch changed meanwhile; otherwise the user's choice stays.
        if (_ops.Tab.Refs.ActiveBranch != _activeAtOpen) Build();
        else Validate();
    }

    void Build()
    {
        var active = _ops.ActiveBranch;
        _activeAtOpen = _ops.Tab.Refs.ActiveBranch;
        Into = active?.ShortName;
        _building = true;
        Remotes = _ops.RemoteNames;
        SelectedRemote = _ops.PickRemote(_preselected is null ? null : _ops.RemoteOf(_preselected), _ops.UpstreamRemote(active));
        _building = false;
        BuildBranches();
    }

    partial void OnSelectedRemoteChanged(string? value)
    {
        if (!_building) BuildBranches();
    }

    partial void OnSelectedBranchChanged(GitRef? value) => Validate();

    void BuildBranches()
    {
        var remote = SelectedRemote;
        Branches = remote is null ? [] : _ops.RemoteBranchesOf(remote);
        var active = _ops.ActiveBranch;
        var preName = _preselected is null ? null : _ops.NameInRemote(_preselected);
        string NameIn(GitRef b) => b.FullName[$"refs/remotes/{remote}/".Length..];
        SelectedBranch = Branches.FirstOrDefault(b => b.FullName == _preselected)
            ?? Branches.FirstOrDefault(b => preName is not null && NameIn(b) == preName)
            ?? Branches.FirstOrDefault(b => b.FullName == active?.Upstream)
            ?? Branches.FirstOrDefault(b => NameIn(b) == active?.ShortName);
        Validate();
    }

    void Validate() => CanSubmit = _loaded && Into is not null && SelectedRemote is not null && SelectedBranch is not null;

    protected override Task OnSubmitAsync()
    {
        Settings.Set(RemoteOps.RebaseKey, Rebase);
        Settings.Set(RemoteOps.AutoStashKey, AutoStash);
        Close(true);
        var remote = SelectedRemote!;
        var branch = SelectedBranch!.FullName;
        // The configured upstream is pulled without a branch argument.
        string? arg = branch == _ops.ActiveBranch?.Upstream ? null : branch[$"refs/remotes/{remote}/".Length..];
        return _ops.PullAsync(remote, arg, branch, Rebase, AutoStash);
    }
}

public enum PushTargetKind { Default, Separator, Branch, Custom, CustomMenu }

/// <summary>An item of the Push dialog's "To:" list. Destination null = no destination (bare source ref).</summary>
public sealed class PushTarget(PushTargetKind kind, string text, string? destination = null)
{
    public PushTargetKind Kind { get; } = kind;
    public string Text { get; } = text;
    public string? Destination { get; } = destination;
    public bool IsSeparator => Kind == PushTargetKind.Separator;
    public bool IsSelectable => !IsSeparator;
    public string? Icon => Kind is PushTargetKind.Branch or PushTargetKind.Custom ? "git-branch" : null;
    public override string ToString() => Text;
}

/// <summary>"Push dialog" with its target list and refspec rules.
/// Not built yet: the submodule "unpushed changes" warning (hidden check job).</summary>
public sealed partial class PushViewModel : DialogViewModel
{
    /// <summary>The only entry of the remote list when the repository has no remote.</summary>
    public const string AddRemoteItem = "Add Remote...";

    readonly RemoteOps _ops;

    /// <summary>Host icon per remote name for the remote drop-down (view root: `c:RefView.HostOf`).</summary>
    public Func<string, HostType> HostOf => _ops.HostOf;
    string? _custom;
    bool _init = true, _rebuilding;

    public PushViewModel(RemoteOps ops, string? branch, string? remote)
    {
        _ops = ops;
        Title = "Push";
        Description = "Push your local changes to remote repository";
        SubmitText = "Push";
        LocalBranches = ops.LocalBranches;
        PushTags = Settings.Get(RemoteOps.PushTagsKey, false);
        LoadRemotes();
        SelectedLocal = LocalBranches.FirstOrDefault(b => b.ShortName == branch)
            ?? LocalBranches.FirstOrDefault(b => b.FullName == ops.Tab.Refs.ActiveBranch)
            ?? LocalBranches.FirstOrDefault();
        // The selected branch's upstream remote (rather than the active branch's; they differ only when the
        // caller passes another branch without a remote, and then the selected branch's own upstream is the useful one).
        SelectedRemote = RealRemotes ? ops.PickRemote(remote, ops.UpstreamRemote(SelectedLocal), RecentRemote) : null;
        CreateTracking = true;
        _init = false;
        RebuildTargets();
    }

    /// <summary>The dialog opened from "Custom..." or "Add Remote..." (tests await it).</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    public IReadOnlyList<GitRef> LocalBranches { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTracking))]
    public partial GitRef? SelectedLocal { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRemote))]
    public partial IReadOnlyList<string> Remotes { get; private set; } = [];

    /// <summary>Label and drop-down are hidden with exactly one remote.</summary>
    public bool ShowRemote => Remotes.Count != 1 || !RealRemotes;

    [ObservableProperty]
    public partial string? SelectedRemote { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<PushTarget> Targets { get; private set; } = [];

    [ObservableProperty]
    public partial PushTarget? SelectedTarget { get; set; }

    /// <summary>"Create tracking reference" is shown only for a branch without an upstream.</summary>
    public bool ShowTracking => SelectedLocal is { Upstream: null };

    [ObservableProperty]
    public partial bool CreateTracking { get; set; }

    [ObservableProperty]
    public partial bool PushTags { get; set; }

    [ObservableProperty]
    public partial bool Force { get; set; }

    public static string ForceTip =>
        "Overwrite the remote branch even if it's not an ancestor of the local branch.\n" +
        "- Force push is required for rebase of already published branch.\n" +
        "- Blindly using force push can be dangerous as you can overwrite other users' commits.\n" +
        "- The app always uses --force-with-lease which protects from race conditions.";

    bool RealRemotes => Remotes is not [AddRemoteItem];
    string? RecentRemote => Settings.Get<string?>(_ops.RecentRemoteKey, null);

    void LoadRemotes()
    {
        var names = _ops.RemoteNames;
        Remotes = names.Count == 0 ? [AddRemoteItem] : names;
    }

    partial void OnSelectedLocalChanged(GitRef? value)
    {
        if (_init) return;
        _custom = null;
        CreateTracking = value is { Upstream: null };
        if (RealRemotes) SelectedRemote = _ops.PickRemote(_ops.UpstreamRemote(value), RecentRemote);
        RebuildTargets();
    }

    partial void OnSelectedRemoteChanged(string? oldValue, string? newValue)
    {
        if (_init) return;
        if (newValue == AddRemoteItem) Pending = AddRemoteAsync(oldValue);
        else RebuildTargets();
    }

    partial void OnSelectedTargetChanged(PushTarget? oldValue, PushTarget? newValue)
    {
        if (_rebuilding) return;
        if (newValue?.Kind == PushTargetKind.CustomMenu) Pending = CustomAsync(oldValue);
        Validate();
    }

    void Validate() =>
        CanSubmit = SelectedLocal is not null && SelectedRemote is { } r && r != AddRemoteItem &&
                    SelectedTarget is { Kind: not (PushTargetKind.Separator or PushTargetKind.CustomMenu) };

    /// <summary>Target list: [default/new, separator], remote branches, separator, custom, "Custom...".</summary>
    void RebuildTargets()
    {
        _rebuilding = true;
        var list = new List<PushTarget>();
        PushTarget? first = null, matched = null, custom = null;
        if (SelectedLocal is { } local && SelectedRemote is { } remote && remote != AddRemoteItem)
        {
            var prefix = $"refs/remotes/{remote}/";
            var branches = _ops.RemoteBranchesOf(remote);
            var items = branches.Select(b => new PushTarget(PushTargetKind.Branch, b.ShortName, "refs/heads/" + b.FullName[prefix.Length..])).ToList();
            if (local.Upstream is { } up && branches.Any(b => b.FullName == up))
                first = new PushTarget(PushTargetKind.Default, $"default ({RemoteOps.ShortRef(up)})");
            else
            {
                var wanted = local.Upstream is { } u ? _ops.NameInRemote(u) : local.ShortName;
                matched = items.FirstOrDefault(i => i.Destination == "refs/heads/" + wanted);
                if (matched is null) first = new PushTarget(PushTargetKind.Default, $"new ({remote}/{wanted})");
            }
            if (first is not null) list.AddRange([first, new PushTarget(PushTargetKind.Separator, "")]);
            list.AddRange(items);
            if (items.Count > 0) list.Add(new PushTarget(PushTargetKind.Separator, ""));
            if (_custom is { } c)
            {
                bool full = c.StartsWith("refs/", StringComparison.Ordinal);
                custom = new PushTarget(PushTargetKind.Custom, full ? c : $"{remote}/{c}", full ? c : "refs/heads/" + c);
                list.Add(custom);
            }
            list.Add(new PushTarget(PushTargetKind.CustomMenu, "Custom..."));
        }
        Targets = list;
        SelectedTarget = custom ?? first ?? matched;
        _rebuilding = false;
        Validate();
    }

    async Task CustomAsync(PushTarget? previous)
    {
        await Task.Yield(); // leave the selection change first
        var vm = new CustomBranchNameViewModel(SelectedRemote!, Settings.Get<string?>(_ops.PushRefspecKey, null) ?? SelectedLocal!.ShortName);
        if (await _ops.Tab.ShowDialog(vm))
        {
            Settings.Set(_ops.PushRefspecKey, vm.Name);
            _custom = vm.Name;
            RebuildTargets();
        }
        else SelectedTarget = previous;
    }

    async Task AddRemoteAsync(string? previous)
    {
        await Task.Yield();
        var add = new RemoteViewModel(_ops, null);
        if (!await _ops.Tab.ShowDialog(add))
        {
            SelectedRemote = previous;
            return;
        }
        await add.Completion; // error window first, then the reloaded list
        _init = true;
        LoadRemotes();
        SelectedRemote = RealRemotes ? Remotes[0] : null;
        _init = false;
        RebuildTargets();
    }

    protected override Task OnSubmitAsync()
    {
        var local = SelectedLocal!.ShortName;
        var remote = SelectedRemote!;
        Settings.Set(RemoteOps.PushTagsKey, PushTags);
        if (_ops.Tab.Remotes.Count > 1) Settings.Set(_ops.RecentRemoteKey, remote);
        Close(true);
        var dst = SelectedTarget!.Destination;
        var refspec = dst is null ? "refs/heads/" + local : $"refs/heads/{local}:{dst}";
        return _ops.PushAsync(local, remote, refspec, Force, PushTags, ShowTracking && CreateTracking);
    }
}

/// <summary>Custom remote branch name dialog: always enabled, git validates.</summary>
public sealed partial class CustomBranchNameViewModel : DialogViewModel
{
    public CustomBranchNameViewModel(string remote, string initial)
    {
        Title = "Custom Remote Branch Name";
        Description = "Enter custom destination";
        SubmitText = "Add";
        Width = 500;
        Prefix = remote + "/";
        Name = initial;
    }

    public string Prefix { get; }

    [ObservableProperty]
    public partial string Name { get; set; }
}

public sealed record PushBranchRow(string Branch, string Target);

/// <summary>Push several branches dialog: read-only list; the targets shown are informational.</summary>
public sealed class PushBranchesViewModel : DialogViewModel
{
    readonly RemoteOps _ops;

    /// <summary>Host icon per remote name for the remote drop-down (view root: `c:RefView.HostOf`).</summary>
    public Func<string, HostType> HostOf => _ops.HostOf;
    readonly IReadOnlyList<string> _branches;

    public PushBranchesViewModel(RemoteOps ops, IReadOnlyList<string> branches, string remote)
    {
        _ops = ops;
        _branches = branches;
        Remote = remote;
        Title = "Push";
        Description = $"Push {branches.Count} branches to remote repository";
        SubmitText = $"Push {branches.Count} branches";
        (Width, Height, CanResize, MinWidth, MinHeight) = (520, 270, true, 520, 170);
        Rows = branches.Select(b => new PushBranchRow(b,
            ops.LocalBranch(b) is { UpstreamState: UpstreamState.Valid, Upstream: { } up } && up.StartsWith($"refs/remotes/{remote}/", StringComparison.Ordinal)
                ? RemoteOps.ShortRef(up) : $"{remote}/{b} (new)")).ToList();
    }

    public string Remote { get; }
    public IReadOnlyList<PushBranchRow> Rows { get; }

    protected override Task OnSubmitAsync()
    {
        Close(true);
        return _ops.PushBranchesAsync(_branches, Remote);
    }
}

/// <summary>Push tag dialog (one tag) and "Push several tags dialog".</summary>
public sealed partial class PushTagsViewModel : DialogViewModel
{
    readonly RemoteOps _ops;

    /// <summary>Host icon per remote name for the remote drop-down (view root: `c:RefView.HostOf`).</summary>
    public Func<string, HostType> HostOf => _ops.HostOf;

    public PushTagsViewModel(RemoteOps ops, IReadOnlyList<string> tags, string? remote)
    {
        _ops = ops;
        Tags = tags;
        if (IsSingle)
        {
            Title = "Push Tag";
            Description = "Push tag to remote repository";
            SubmitText = "Push";
            MinHeight = 190;
        }
        else
        {
            Title = "Push";
            Description = $"Push {tags.Count} tags to remote repository";
            SubmitText = $"Push {tags.Count} tags";
            (Width, Height, CanResize, MinWidth, MinHeight) = (520, 270, true, 520, 170);
        }
        Remotes = ops.RemoteNames;
        SelectedRemote = ops.PickRemote(remote, ops.UpstreamRemote(ops.ActiveBranch));
        CanSubmit = SelectedRemote is not null;
    }

    public IReadOnlyList<string> Tags { get; }
    public bool IsSingle => Tags.Count == 1;
    public bool IsMany => !IsSingle;
    public string Tag => Tags[0];
    public IReadOnlyList<string> Remotes { get; }

    [ObservableProperty]
    public partial string? SelectedRemote { get; set; }

    partial void OnSelectedRemoteChanged(string? value) => CanSubmit = value is not null;

    protected override Task OnSubmitAsync()
    {
        Close(true);
        return _ops.PushTagsAsync(Tags, SelectedRemote!);
    }
}

/// <summary>An item of the "Track:" list: "No tracking" (FullName null), a separator, or a remote branch.</summary>
public sealed class TrackItem(string? fullName, string text, bool isSeparator = false)
{
    public string? FullName { get; } = fullName;
    public string Text { get; } = text;
    public bool IsSeparator { get; } = isSeparator;
    public bool IsSelectable => !IsSeparator;
    public string? Icon => FullName is null ? null : "git-branch";
    public override string ToString() => Text;
}

/// <summary>
/// Change tracking reference dialog; stays open while its job runs.
/// An upstream that no longer exists preselects "No tracking" with Change enabled, which removes it.
/// </summary>
public sealed partial class ChangeTrackingViewModel : DialogViewModel
{
    readonly RemoteOps _ops;
    readonly string? _upstream;

    public ChangeTrackingViewModel(RemoteOps ops, GitRef branch)
    {
        _ops = ops;
        _upstream = branch.Upstream;
        Branch = branch.ShortName;
        Title = "Change tracking reference";
        Description = "Change branch remote tracking reference";
        SubmitText = "Change";
        Items = [new TrackItem(null, "No tracking"), new TrackItem(null, "", isSeparator: true),
            .. ops.RemoteBranches.Select(r => new TrackItem(r.FullName, r.ShortName))];
        Selected = Items.FirstOrDefault(i => i.FullName is not null && i.FullName == _upstream) ?? Items[0];
        Validate();
    }

    public string Branch { get; }
    public IReadOnlyList<TrackItem> Items { get; }

    [ObservableProperty]
    public partial TrackItem? Selected { get; set; }

    partial void OnSelectedChanged(TrackItem? value) => Validate();

    // Disabled while the selection equals the current state.
    void Validate() => CanSubmit = Selected is { IsSeparator: false } s && s.FullName != _upstream;

    protected override Task OnSubmitAsync()
    {
        var target = Selected!.FullName;
        SetStatus(DialogStatus.InProgress, target is null ? "Removing tracking reference..." : "Updating tracking reference...");
        return _ops.SetUpstreamAsync(Branch, target, this);
    }
}

/// <summary>
/// Add remote and Edit remote dialog; stays open while its job runs.
/// Not built yet: no Test Connection, HTTPS/SSH protocol switch, Account drop-down, submodule URL sync or clipboard prefill
/// (the Clone dialog has the switch, Test Connection and the account selector; this dialog can reuse
/// `RemoteUrl` / `CloneAccounts` later; SSH keys later).
/// </summary>
public sealed partial class RemoteViewModel : DialogViewModel
{
    readonly RemoteOps _ops;
    readonly RemoteEntry? _edit;

    public RemoteViewModel(RemoteOps ops, RemoteEntry? edit)
    {
        _ops = ops;
        _edit = edit;
        Title = edit is null ? "Add Remote" : "Edit Remote";
        Description = edit is null ? "Add new remote repository reference" : "Edit URL of the remote repository";
        SubmitText = edit is null ? "Add New Remote" : "Edit";
        Width = 630;
        MinHeight = 220;
        Name = edit?.Name ?? "origin";
        Url = edit?.Url ?? "";
        Validate();
    }

    /// <summary>The add/edit job (the Push dialog waits for it before reloading its remotes).</summary>
    public Task Completion { get; private set; } = Task.CompletedTask;

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    public partial string Url { get; set; }

    partial void OnNameChanged(string value) => Validate();
    partial void OnUrlChanged(string value) => Validate();

    void Validate()
    {
        string name = (Name ?? "").Trim(), url = (Url ?? "").Trim();
        if (name.Length == 0 || url.Length == 0 || (_edit is not null && name == _edit.Name && url == _edit.Url))
        {
            SetValid(false);
            return;
        }
        var (ok, msg) = RefName.Validate(name);
        if (ok && name != _edit?.Name && _ops.Tab.Remotes.Any(r => r.Name == name))
            (ok, msg) = (false, $"Remote '{name}' already exists");
        SetValid(ok, msg);
    }

    protected override Task OnSubmitAsync()
    {
        string name = Name.Trim(), url = Url.Trim();
        var repo = _ops.Tab.Repo;
        if (_edit is { } e)
        {
            SetStatus(DialogStatus.InProgress, "Editing remote...");
            return Completion = _ops.Tab.RunJobAsync($"Edit remote '{e.Name}'",
                j => repo.EditRemoteAsync(j, e.Name, e.Url, name, url), dialog: this);
        }
        SetStatus(DialogStatus.InProgress, "Adding remote...");
        return Completion = _ops.Tab.RunJobAsync($"Add remote '{name}'",
            async j => await repo.AddRemoteAsync(j, name, url), dialog: this);
    }
}
