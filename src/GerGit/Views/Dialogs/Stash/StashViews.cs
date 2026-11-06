using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using GerGit.ViewModels.Dialogs;

namespace GerGit.Views.Dialogs;

// Bodies of the stash, cherry-pick and revert dialogs; hosted by DialogWindow.

public partial class SaveStashView : UserControl
{
    public SaveStashView() => InitializeComponent();
}

public partial class StashFilesView : UserControl
{
    public StashFilesView() => InitializeComponent();

    /// <summary>The list scrolls to the first checked row.</summary>
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is not StashFilesViewModel vm) return;
        var first = vm.Files.ToList().FindIndex(f => f.IsChecked);
        if (first > 0) Dispatcher.UIThread.Post(() => FileList.ContainerFromIndex(first)?.BringIntoView(), DispatcherPriority.Loaded);
    }
}

public partial class ApplyStashView : UserControl
{
    public ApplyStashView() => InitializeComponent();
}

public partial class RenameStashView : UserControl
{
    public RenameStashView() => InitializeComponent();
}

public partial class DeleteStashView : UserControl
{
    public DeleteStashView() => InitializeComponent();
}

public partial class CommitPickView : UserControl
{
    public CommitPickView() => InitializeComponent();
}
