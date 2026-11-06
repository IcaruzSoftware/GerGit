using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using GerGit.Controls;
using GerGit.Core;
using GerGit.ViewModels;

namespace GerGit.Views;

public partial class DetailsView : UserControl
{
    public DetailsView()
    {
        InitializeComponent();
        // Keyboard and mouse: Ctrl+↑ / Ctrl+↓ in the file list or the editor = previous / next change.
        // Tunnel so the list and the editor do not consume the keys first.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // File menu in the Changes tab / "File menu on rows in the Commit tab".
        FileList.ContextRequested += (_, e) => OpenMenu(e, FileList, SelectedFiles, commitTab: false);
        CommitFiles.ContextRequested += (_, e) =>
            OpenMenu(e, CommitFiles, (e.Source as Control)?.DataContext is ChangedFile f ? [f] : [], commitTab: true);
        // The view model is listened to only while the view is attached (a detached view must not keep it alive or
        // keep reacting); attaching shows the message of the commit that is current then.
        DataContextChanged += (_, _) =>
        {
            Listen(_attached ? Vm : null);
            ShowMessage();
        };
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            IssueLinks.Changed += ShowMessage;
            Listen(Vm);
            ShowMessage();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            IssueLinks.Changed -= ShowMessage;
            Listen(null);
        };
    }

    DetailsViewModel? _shownVm;
    bool _attached;

    void Listen(DetailsViewModel? vm)
    {
        if (_shownVm is { } old) old.PropertyChanged -= OnVmPropertyChanged;
        _shownVm = vm;
        if (vm is not null) vm.PropertyChanged += OnVmPropertyChanged;
    }

    void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DetailsViewModel.Commit)) ShowMessage();
    }

    /// <summary>The message links of the shown commit are set (tests await it).</summary>
    internal Task MessageShown { get; private set; } = Task.CompletedTask;

    /// <summary>Subject and description: the plain text at once, then the issue tracker
    /// links and URLs, computed off the UI thread (rule files and patterns come from the repository).
    /// Limitation: no links in the compact header subject.</summary>
    void ShowMessage() => MessageShown = ShowMessageAsync();

    int _messageVersion;
    CancellationTokenSource? _messageCts;

    async Task ShowMessageAsync()
    {
        // Each call cancels the previous search and gets its own version, so an older result never replaces a newer one.
        int version = ++_messageVersion;
        _messageCts?.Cancel();
        var cts = _messageCts = new CancellationTokenSource();
        var commit = Vm?.Commit;
        string subject = commit?.Subject ?? "", description = commit?.Description ?? "";
        Hyperlinks.SetContent(SubjectText, new(subject, []));
        Hyperlinks.SetContent(DescriptionText, new(description, []));
        if (commit is null || Vm is not { } vm) return;
        List<TextLink>[] links;
        try { links = await IssueLinks.FindAsync(vm.Owner.Repo.Info, cts.Token, subject, description); }
        catch (OperationCanceledException) { return; }
        if (version != _messageVersion) return;
        Hyperlinks.SetContent(SubjectText, new(subject, links[0]));
        Hyperlinks.SetContent(DescriptionText, new(description, links[1]));
    }

    DetailsViewModel? Vm => DataContext as DetailsViewModel;

    /// <summary>The selected files in list order (not click order).</summary>
    List<ChangedFile> SelectedFiles
    {
        get
        {
            var selected = FileList.SelectedItems?.OfType<ChangedFile>().ToHashSet() ?? [];
            return Vm?.Files.Where(selected.Contains).ToList() ?? [];
        }
    }

    Task Copy(string text) => TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? Task.CompletedTask;

    Task<string?> PickSaveFile(string name, string? folder) =>
        TopLevel.GetTopLevel(this) is { } top ? FileDialogs.SaveFileAsync(top, name, folder) : Task.FromResult<string?>(null);

    /// <summary>The menu of these files; none when nothing is selected.</summary>
    internal IReadOnlyList<MenuEntry> FileMenu(IReadOnlyList<ChangedFile> files, bool commitTab) =>
        Vm is { } vm ? CommitFileMenu.Build(vm, files, commitTab, Copy, PickSaveFile) : [];

    void OpenMenu(ContextRequestedEventArgs e, Control target, IReadOnlyList<ChangedFile> files, bool commitTab)
    {
        e.Handled = true;
        if (FileMenu(files, commitTab) is { Count: > 0 } menu) MenuControls.Open(target, menu);
    }

    void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (FileList.IsKeyboardFocusWithin && Vm is { } vm && OnListKey(vm, e)) return;
        if (e.KeyModifiers != KeyModifiers.Control || e.Key is not (Key.Up or Key.Down) || !Diff.IsEffectivelyVisible) return;
        Diff.GoToChange(e.Key == Key.Down);
        e.Handled = true;
    }

    /// <summary>Keyboard and mouse, Changes list: Ctrl+C / Ctrl+Shift+C copy paths, Ctrl+Alt+Shift+O opens the
    /// file as of the commit, Ctrl+D runs the external diff.</summary>
    bool OnListKey(DetailsViewModel vm, KeyEventArgs e)
    {
        var m = e.KeyModifiers;
        if (e.Key == Key.C && m == KeyModifiers.Control) CommitFileMenu.CopyPathsAsync(vm, SelectedFiles, false, Copy).Forget();
        else if (e.Key == Key.C && m == (KeyModifiers.Control | KeyModifiers.Shift)) CommitFileMenu.CopyPathsAsync(vm, SelectedFiles, true, Copy).Forget();
        else if (e.Key == Key.O && m == (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift)) CommitFileMenu.OpenAtCommitAsync(vm, SelectedFiles).Forget();
        else if (e.Key == Key.D && m == KeyModifiers.Control) CommitFileMenu.ExternalDiffAsync(vm, SelectedFiles).Forget();
        else return false;
        e.Handled = true;
        return true;
    }

    // Changes tab change-type icons (Lucide) and colours.
    public static readonly IValueConverter KindIcon = new FuncValueConverter<ChangeKind, string>(k => k switch
    {
        ChangeKind.Added or ChangeKind.Untracked => "file-plus",
        ChangeKind.Deleted => "file-minus",
        ChangeKind.Renamed => "file-symlink",
        ChangeKind.Copied => "copy",
        ChangeKind.Conflicted => "triangle-alert",
        _ => "file-pen",
    });

    // FileStatus colours are theme-independent (Colors.axaml ), so a lookup at bind time is enough.
    public static readonly IValueConverter KindBrush = new FuncValueConverter<ChangeKind, IBrush?>(k =>
        Application.Current!.TryFindResource("Color.FileStatus." + k switch
        {
            ChangeKind.Added or ChangeKind.Untracked => "Added",
            ChangeKind.Deleted => "Deleted",
            ChangeKind.Renamed => "Renamed",
            ChangeKind.Copied => "Copied",
            ChangeKind.Conflicted => "Conflict",
            _ => "Modified",
        }, out var b) ? b as IBrush : null);

    public static readonly IValueConverter FileText = new FuncValueConverter<ChangedFile, string>(f =>
        f is null ? "" : f.OldPath is { } old && old != f.Path ? $"{old} → {f.Path}" : f.Path);

    static bool IsRenamed(ChangedFile? f) => f is { OldPath: { } old } && old != f.Path;

    /// <summary>Renamed (and copied) files always get "Old:\t…\nNew:\t…" on the row.</summary>
    public static readonly IValueConverter FileToolTip = new FuncValueConverter<ChangedFile, string?>(f =>
        IsRenamed(f) ? $"Old:\t{f!.OldPath}\nNew:\t{f.Path}" : null);

    /// <summary>Other rows get the full path as tooltip only when it is trimmed.
    /// The rows of the Commit tab share this template.</summary>
    public static readonly IValueConverter IsNotRenamed = new FuncValueConverter<ChangedFile, bool>(f => f is not null && !IsRenamed(f));

    public static readonly IValueConverter Sha7 = new FuncValueConverter<string, string>(s => s is null ? "" : s[..Math.Min(7, s.Length)]);
}
