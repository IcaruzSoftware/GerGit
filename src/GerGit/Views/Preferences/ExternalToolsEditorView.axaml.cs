using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GerGit.ViewModels;
using GerGit.ViewModels.Preferences;

namespace GerGit.Views.Preferences;

/// <summary>The external merge / diff tools editor of the Integration page.</summary>
public partial class ExternalToolsEditorView : UserControl
{
    public ExternalToolsEditorView() => InitializeComponent();

    ExternalToolsEditorViewModel? Vm => DataContext as ExternalToolsEditorViewModel;

    void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.AddCustom();
        ToolList.ScrollIntoView(vm.Selected!);
    }

    async void OnRemove(object? sender, RoutedEventArgs e) => await Safe(() => Vm?.RemoveAsync() ?? Task.CompletedTask);

    /// <summary>The row under the pointer is selected and gets the Primary / Visible / Reset to default menu.</summary>
    void OnRowMenu(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        if (Vm is not { } vm || (e.Source as Control)?.DataContext is not ExternalTool tool) return;
        vm.Selected = tool;
        MenuControls.Open(ToolList, vm.RowMenu(tool));
    }

    async void OnBrowse(object? sender, RoutedEventArgs e) => await Safe(async () =>
    {
        if (Vm is not { Selected: not null } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Select external tool", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) vm.SetPath(path);
    });

    static async Task Safe(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) { Trace.WriteLine(ex); } // a failing chooser or dialog must not end the process
    }
}
