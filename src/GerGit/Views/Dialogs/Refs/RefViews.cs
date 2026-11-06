using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using GerGit.ViewModels.Dialogs.Refs;

namespace GerGit.Views.Dialogs.Refs;

// Bodies of the branch, checkout, merge, reset and tag dialogs; hosted by DialogWindow.

/// <summary>Tracks Shift on the whole dialog.</summary>
public partial class LocalChangesBlockView : UserControl
{
    TopLevel? _top;

    public LocalChangesBlockView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel, handledEventsToo: true);
        _top?.AddHandler(KeyUpEvent, OnKey, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(KeyDownEvent, OnKey);
        _top?.RemoveHandler(KeyUpEvent, OnKey);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.LeftShift or Key.RightShift && DataContext is LocalChangesBlockViewModel vm)
            vm.ShiftHeld = e.RoutedEvent == KeyDownEvent;
    }
}

public partial class CreateBranchView : UserControl
{
    public CreateBranchView() => InitializeComponent();
}

public partial class CheckoutView : UserControl
{
    public CheckoutView() => InitializeComponent();
}

public partial class TrackRemoteBranchView : UserControl
{
    public TrackRemoteBranchView() => InitializeComponent();
}

public partial class CheckoutSyncView : UserControl
{
    public CheckoutSyncView() => InitializeComponent();
}

public partial class RenameBranchView : UserControl
{
    public RenameBranchView() => InitializeComponent();
}

public partial class DeleteRefsView : UserControl
{
    public DeleteRefsView() => InitializeComponent();
}

/// <summary>Keys S, M, H pick Soft, Mixed, Hard.</summary>
public partial class ResetView : UserControl
{
    TopLevel? _top;

    public ResetView() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(KeyDownEvent, OnKey, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(KeyDownEvent, OnKey);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None || DataContext is not ResetViewModel vm || !IsEffectivelyEnabled) return;
        ResetKind? kind = e.Key switch { Key.S => ResetKind.Soft, Key.M => ResetKind.Mixed, Key.H => ResetKind.Hard, _ => null };
        if (kind is null) return;
        vm.Kind = kind.Value;
        e.Handled = true;
    }
}

public partial class MergeView : UserControl
{
    public MergeView() => InitializeComponent();
}

public partial class CreateTagView : UserControl
{
    public CreateTagView() => InitializeComponent();
}

public partial class TagDetailsView : UserControl
{
    public TagDetailsView() => InitializeComponent();
}
