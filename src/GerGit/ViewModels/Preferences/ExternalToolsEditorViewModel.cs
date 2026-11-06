using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.ViewModels.Dialogs;

namespace GerGit.ViewModels.Preferences;

/// <summary>
/// Used twice on the Integration page (merge tools, diff tools). Every change is
/// stored at once, like the rest of the Preferences window.
/// A found tool shows a Lucide icon (Linux has no executable icons) and the empty state is an icon. Not built yet: an
/// "executables only" filter in the browse dialog.
/// </summary>
public sealed partial class ExternalToolsEditorViewModel : ObservableObject
{
    readonly Func<DialogViewModel, Task<bool>> _showDialog;

    public ExternalToolsEditorViewModel(ToolKind kind, Func<DialogViewModel, Task<bool>> showDialog)
    {
        Kind = kind;
        _showDialog = showDialog;
        // Found tools first, then the rest; alphabetical within.
        foreach (var t in ExternalTools.Load(kind).OrderBy(t => !t.IsAvailable).ThenBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            Track(t);
        Selected = Tools.FirstOrDefault();
    }

    public ToolKind Kind { get; }

    /// <summary>The "?" hint: the variables of this kind.</summary>
    public string Hint => ExternalTools.Hint(Kind);

    /// <summary>The row icon of a found tool.</summary>
    public string Icon => Kind == ToolKind.Diff ? "file-diff" : "git-merge";

    public ObservableCollection<ExternalTool> Tools { get; } = [];

    public bool HasTools => Tools.Count > 0;

    [ObservableProperty] public partial ExternalTool? Selected { get; set; }

    void Track(ExternalTool t)
    {
        t.PropertyChanged += (_, _) => Save();
        Tools.Add(t);
        OnPropertyChanged(nameof(HasTools));
    }

    void Save() => ExternalTools.Save(Kind, Tools);

    /// <summary>`plus`: a custom tool named `Custom`, or `Custom&lt;N&gt;` with N = the number of tools whose name starts
    /// with "custom" (any case), selected.</summary>
    public void AddCustom()
    {
        int n = Tools.Count(t => t.Name.StartsWith("custom", StringComparison.OrdinalIgnoreCase));
        var tool = new ExternalTool(Kind, n > 0 ? $"Custom{n}" : "Custom", "", [], isCustom: true);
        Track(tool);
        Selected = tool;
    }

    /// <summary>`minus`, after the confirmation: removed from the page (a predefined tool comes back at the next start
    /// with its defaults); then the previous entry, or the first, is selected.</summary>
    public async Task RemoveAsync()
    {
        if (Selected is not { } tool) return;
        var confirm = new DialogViewModel
        {
            Title = "Do you want to remove the selected external tool?",
            Description = "You can't undo this action",
            SubmitText = "Remove",
            Width = 550,
        };
        if (!await _showDialog(confirm)) return;
        int i = Tools.IndexOf(tool);
        if (i < 0) return;
        Tools.RemoveAt(i);
        OnPropertyChanged(nameof(HasTools));
        Save();
        Selected = Tools.Count == 0 ? null : Tools[Math.Max(0, i - 1)];
    }

    /// <summary>Browse: the chosen executable becomes the tool's path.</summary>
    public void SetPath(string path)
    {
        if (Selected is { } t) t.PathOverride = path;
    }

    /// <summary>Row menu: `Primary` (one at most; enabled when the tool is available or already primary), `Visible`,
    /// and for predefined tools `Reset to default`.</summary>
    public List<MenuEntry> RowMenu(ExternalTool t) => Menus.Join(
        [
            new MenuEntry { Header = "Primary", IsChecked = t.IsPrimary, IsEnabled = t.IsAvailable || t.IsPrimary, Action = () => Run(() => TogglePrimary(t)) },
            new MenuEntry { Header = "Visible", IsChecked = t.IsVisible, Action = () => Run(() => t.IsVisible = !t.IsVisible) },
        ],
        t.IsPredefined ? [Menus.Item("Reset to default", () => Run(t.ResetToDefault))] : []);

    static Task Run(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    void TogglePrimary(ExternalTool t)
    {
        bool on = !t.IsPrimary;
        if (on) foreach (var other in Tools.Where(o => o != t)) other.IsPrimary = false;
        t.IsPrimary = on;
    }
}
