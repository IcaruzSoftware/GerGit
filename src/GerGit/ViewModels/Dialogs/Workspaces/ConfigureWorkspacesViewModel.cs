using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GerGit.ViewModels.Dialogs;

/// <summary>A row of the Configure Workspaces list; <see cref="Original"/> is null for a new workspace.</summary>
public sealed partial class WorkspaceItem(Workspace? original, string name) : ObservableObject
{
    public Workspace? Original { get; } = original;

    [ObservableProperty]
    public partial string Name { get; set; } = name;

    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditText { get; set; } = "";

    public override string ToString() => Name;
}

/// <summary>Configure Workspaces: add, rename and remove workspaces (at least two stay), and "Show workspace
/// name in title bar" (applied at once). The caller stores <see cref="Outcome"/> when the window closes.</summary>
public sealed partial class ConfigureWorkspacesViewModel : DialogViewModel
{
    readonly MainWindowViewModel _main;

    public ConfigureWorkspacesViewModel(MainWindowViewModel main, IEnumerable<Workspace> workspaces)
    {
        _main = main;
        Title = "Workspaces";
        Description = "Use '/' as path separator to create folders";
        SubmitText = null;
        CancelText = "Close";
        Width = 330;
        Height = 370;
        MinWidth = 250;
        MinHeight = 300;
        CanResize = true;
        foreach (var w in workspaces.OrderBy(w => w.Name, RemoteOps.Natural)) Items.Add(new(w, w.Name));
        SelectedItem = Items.FirstOrDefault();
    }

    public ObservableCollection<WorkspaceItem> Items { get; } = [];

    [ObservableProperty]
    public partial WorkspaceItem? SelectedItem { get; set; }

    /// <summary>"-" and "Delete..." need more than two workspaces.</summary>
    public bool CanRemove => Items.Count > 2;

    public bool ShowInTitle
    {
        get => _main.ShowWorkspaceInTitle;
        set
        {
            _main.ShowWorkspaceInTitle = value;
            OnPropertyChanged();
        }
    }

    /// <summary>The delete confirmation over this window; tests answer it.</summary>
    internal Func<DialogViewModel, Task<bool>> ShowDialog { get; set; } = vm => Views.Dialogs.DialogWindow.ShowAsync(vm);

    /// <summary>"+" / "Add New Workspace": a new row named "New Workspace", selected and in edit mode. A number
    /// is appended while the name is taken, since duplicate names are rejected.</summary>
    public void Add()
    {
        var name = "New Workspace";
        for (int n = 2; Taken(name, null); n++) name = $"New Workspace {n}";
        var item = new WorkspaceItem(null, name);
        Items.Add(item);
        SelectedItem = item;
        BeginRename(item);
        OnPropertyChanged(nameof(CanRemove));
    }

    bool Taken(string name, WorkspaceItem? except) =>
        Items.Any(i => i != except && string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    public void BeginRename(WorkspaceItem item)
    {
        item.EditText = item.Name;
        item.IsEditing = true;
    }

    /// <summary>Enter or leaving the box: a blank name, or one with an empty "/" part, is rejected and the previous one
    /// kept. A name another workspace has is rejected too (ignoring case, as the menu folders match), since
    /// workspaces are found by name.</summary>
    public void CommitRename(WorkspaceItem item)
    {
        if (!item.IsEditing) return;
        item.IsEditing = false;
        var name = item.EditText.Trim();
        // An empty "/" part ("a/", "a//b", "/a") would make a nameless menu folder or item.
        if (name.Split('/').All(p => p.Trim().Length > 0) && !Taken(name, item)) item.Name = name;
    }

    public void CancelRename(WorkspaceItem item) => item.IsEditing = false;

    /// <summary>"-" / "Delete...": after the confirmation the row goes; the row at the same index (or the first) is
    /// selected.</summary>
    public async Task RemoveAsync(WorkspaceItem item)
    {
        if (!CanRemove) return;
        var confirm = new DialogViewModel
        {
            Title = $"Do you want to delete the workspace '{item.Name}'?",
            Description = "You can't undo this action",
            SubmitText = "Delete",
            Width = 500,
        };
        if (!await ShowDialog(confirm)) return;
        int index = Items.IndexOf(item);
        Items.Remove(item);
        SelectedItem = index < Items.Count ? Items[index] : Items.FirstOrDefault();
        OnPropertyChanged(nameof(CanRemove));
    }

    /// <summary>The workspaces to store (renamed ones keep their tabs) and the new name of <paramref name="active"/>,
    /// null when it was deleted.</summary>
    public (List<Workspace> List, string? Active) Outcome(string active)
    {
        foreach (var i in Items) CommitRename(i);
        var list = Items.Select(i => (i.Original ?? new Workspace(i.Name, [])) with { Name = i.Name }).ToList();
        return (list, Items.FirstOrDefault(i => i.Original?.Name == active)?.Name);
    }
}
