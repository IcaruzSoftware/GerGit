using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GerGit.Hosting;

namespace GerGit.Controls;

/// <summary>
/// Read-only "ref view" row of the dialogs (icon + name, optional dimmed detail), also the drop-down item template:
/// local branch `git-branch`, remote branch `cloud`, tag `tag`, commit `git-commit-horizontal` (Text = short sha,
/// Detail = subject), stash `archive`. A remote: set <see cref="Remote"/> instead of Icon; the icon is the remote's host
/// icon from the inherited <see cref="HostOfProperty"/> that a
/// dialog view binds once at its root (`c:RefView.HostOf="{Binding HostOf}"`).
/// </summary>
public sealed class RefView : StackPanel
{
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<RefView, string?>(nameof(Icon));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<RefView, string?>(nameof(Text));
    public static readonly StyledProperty<string?> DetailProperty = AvaloniaProperty.Register<RefView, string?>(nameof(Detail));
    public static readonly StyledProperty<string?> RemoteProperty = AvaloniaProperty.Register<RefView, string?>(nameof(Remote));

    /// <summary>Host type of a remote by name; inherits down the tree (also into drop-down popups).</summary>
    public static readonly AttachedProperty<Func<string, HostType>?> HostOfProperty =
        AvaloniaProperty.RegisterAttached<RefView, Control, Func<string, HostType>?>("HostOf", inherits: true);

    public static Func<string, HostType>? GetHostOf(Control c) => c.GetValue(HostOfProperty);
    public static void SetHostOf(Control c, Func<string, HostType>? value) => c.SetValue(HostOfProperty, value);

    readonly LucideIcon _icon = new() { Size = 14, VerticalAlignment = VerticalAlignment.Center };
    readonly HostIcon _host = new() { Size = 14, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
    readonly TextBlock _text = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _detail = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.7 };

    public RefView()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 6;
        Children.Add(_icon);
        Children.Add(_host);
        Children.Add(_text);
        Children.Add(_detail);
        _detail.IsVisible = false;
    }

    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string? Detail { get => GetValue(DetailProperty); set => SetValue(DetailProperty, value); }
    /// <summary>Remote name: shows its host icon instead of <see cref="Icon"/>.</summary>
    public string? Remote { get => GetValue(RemoteProperty); set => SetValue(RemoteProperty, value); }

    /// <summary>The host icon shown for <see cref="Remote"/> (tests).</summary>
    public HostType? ShownHost => Remote is null ? null : _host.Type;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty) _icon.Icon = Icon;
        else if (change.Property == RemoteProperty || change.Property == HostOfProperty)
        {
            _icon.IsVisible = Remote is null;
            _host.IsVisible = Remote is not null;
            _host.Type = Remote is not null && GetHostOf(this) is { } hostOf ? hostOf(Remote) : HostType.Custom;
        }
        else if (change.Property == TextProperty) _text.Text = Text;
        else if (change.Property == DetailProperty)
        {
            _detail.Text = Detail;
            _detail.IsVisible = !string.IsNullOrEmpty(Detail);
        }
    }
}
