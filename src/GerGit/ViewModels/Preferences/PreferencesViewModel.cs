using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core.Git;
using GerGit.ViewModels.Dialogs;
using GerGit.Views.Dialogs;

namespace GerGit.ViewModels.Preferences;

/// <summary>A row of the "Source Code Folder" list.</summary>
public sealed class SourceFolderItem(string path, bool canRemove)
{
    public string Path { get; } = path;

    /// <summary>The row menu's remove item needs more than one folder.</summary>
    public bool CanRemove { get; } = canRemove;

    /// <summary>The filesystem root or the home folder: warning icon and tooltip instead of the folder icon.</summary>
    public bool IsWarning
    {
        get
        {
            var p = Path.TrimEnd('/');
            return p.Length == 0 || p == Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('/');
        }
    }

    public string? WarningTip => IsWarning ? $"'{Path}' should not be used as a source directory. Please choose a subfolder instead" : null;
}

/// <summary>
/// The Preferences window: pages General, Commit, Git and Integration. Every change is stored at once
/// (Settings writes the file on each change), so every way of closing keeps it; the global identity is also written
/// when the window closes.
/// Only the settings GerGit has features for are shown. Not built yet: diff view preview, -+ marks, disabling syntax
/// highlighting, commit sorting, a tab indicator for uncommitted changes, compact branch labels, spell checking,
/// page guide, AI messages, a custom Git instance, verbose Git output and the Custom Commands tab.
/// </summary>
public sealed partial class PreferencesViewModel : DialogViewModel
{
    public PreferencesViewModel()
    {
        Title = "Preferences";
        SubmitText = null;
        CancelText = "Close";
        CancelIsDefault = true; // Close is the default button (Enter) and Esc closes too
        PlainFrame = true;
        Width = 690; // the widest page (Integration, 650) + 40; no re-fit per tab
        LoadFolders();
        var s = AppPreferences.Current;
        _fontSizeText = s.EditorFontSize.ToString(CultureInfo.InvariantCulture);
        _lowLimitText = AppPreferences.SubjectWarnAt.ToString(CultureInfo.InvariantCulture);
        _highLimitText = AppPreferences.SubjectErrorAt.ToString(CultureInfo.InvariantCulture);
        _terminal = s.Terminal;
        _detectedTerminal = OpenIn.DetectTerminal()?.Path ?? "";
        CloseRequested += _ => SaveIdentityAsync().Forget();
        MergeTools = new(ToolKind.Merge, vm => ShowDialog(vm));
        DiffTools = new(ToolKind.Diff, vm => ShowDialog(vm));
        Loading = LoadGitAsync();
    }

    /// <summary>Confirmations and errors over this window; tests answer them.</summary>
    internal Func<DialogViewModel, Task<bool>> ShowDialog { get; set; } = vm => DialogWindow.ShowAsync(vm);

    /// <summary>The git config scope of the global identity; tests point it at a temp file (`--file`). Static: the
    /// constructor already reads it.</summary>
    internal static string[] IdentityScope { get; set; } = ["--global"];

    /// <summary><see cref="IdentityScope"/> when this window opened: every read and write uses the same file.</summary>
    readonly string[] _scope = IdentityScope;

    /// <summary>Git instance text and identity read on open (tests await it).</summary>
    public Task Loading { get; private set; }

    // ------------------------------------------------------------------ General

    public ObservableCollection<SourceFolderItem> SourceFolders { get; } = [];

    [ObservableProperty]
    public partial SourceFolderItem? SelectedFolder { get; set; }

    /// <summary>Remove (button and menu) needs more than one folder.</summary>
    public bool CanRemoveFolder => SourceFolders.Count > 1;

    void LoadFolders(string? select = null)
    {
        SourceFolders.Clear();
        var folders = AppPreferences.SourceFolders;
        foreach (var f in folders.Order(RemoteOps.Natural)) SourceFolders.Add(new(f, folders.Count > 1));
        SelectedFolder = SourceFolders.FirstOrDefault(f => f.Path == select);
        OnPropertyChanged(nameof(CanRemoveFolder));
    }

    static string WithSlash(string path) => Path.EndsInDirectorySeparator(path) ? path : path + "/";

    /// <summary>Add: the chosen folder with a trailing separator, stored at once (an existing entry is not added twice).</summary>
    public void AddFolder(string path)
    {
        path = WithSlash(path);
        var list = AppPreferences.SourceFolders.ToList();
        if (!list.Contains(path)) list.Add(path);
        AppPreferences.SourceFolders = list;
        LoadFolders(path);
    }

    /// <summary>Row button: the old entry is replaced by the chosen folder.</summary>
    public void ReplaceFolder(SourceFolderItem item, string path)
    {
        path = WithSlash(path);
        var list = AppPreferences.SourceFolders.Where(f => f != item.Path && f != path).ToList();
        list.Add(path);
        AppPreferences.SourceFolders = list;
        LoadFolders(path);
    }

    /// <summary>Remove, after the confirmation; the repository entries below the folder are dropped
    /// without being ignored by later scans.</summary>
    public async Task RemoveFolderAsync(SourceFolderItem item)
    {
        if (!CanRemoveFolder) return;
        var confirm = new DialogViewModel
        {
            Title = "Do you want to remove the selected source directory?",
            Description = "GerGit will not look for repositories in this folder automatically",
            SubmitText = "Remove",
            Width = 550,
        };
        if (!await ShowDialog(confirm)) return;
        AppPreferences.SourceFolders = AppPreferences.SourceFolders.Where(f => f != item.Path).ToList();
        RepositoryList.RemoveBelow(item.Path);
        LoadFolders();
    }

    string _fontSizeText;

    /// <summary>Diff font size box: not a number = 13, clamped to 10-40; the box text is not rewritten.</summary>
    public string FontSizeText
    {
        get => _fontSizeText;
        set
        {
            if (!SetProperty(ref _fontSizeText, value)) return;
            AppPreferences.Current.EditorFontSize = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size)
                && double.IsFinite(size) ? size : AppPreferences.DefaultEditorFontSize;
        }
    }

    public bool AutoFetch
    {
        get => Settings.Get(ViewModels.AutoFetch.EnabledKey, true);
        set { Settings.Set(ViewModels.AutoFetch.EnabledKey, value); OnPropertyChanged(); }
    }

    public bool FetchWithTags
    {
        get => Settings.Get(RemoteOps.FetchTagsKey, false);
        set { Settings.Set(RemoteOps.FetchTagsKey, value); OnPropertyChanged(); }
    }

    public bool PushAfterCommit
    {
        get => LocalChangesViewModel.PushAfterCommit;
        set { Settings.Set(LocalChangesViewModel.PushAfterCommitKey, value); OnPropertyChanged(); }
    }

    /// <summary>Only submodules that are initialized and checked out are updated; new ones are never initialized or cloned.</summary>
    public bool UpdateSubmodules
    {
        get => AppPreferences.UpdateSubmodules;
        set { AppPreferences.UpdateSubmodules = value; OnPropertyChanged(); }
    }

    public IReadOnlyList<string> SpaceChars { get; } = ["-", "_"];

    public string SpaceChar
    {
        get => Settings.SpaceReplacement;
        set
        {
            if (value is null) return; // the combo clears its selection while it is torn down
            Settings.Set(Settings.SpaceCharKey, value);
            OnPropertyChanged();
        }
    }

    // ------------------------------------------------------------------ Commit

    string _lowLimitText, _highLimitText;

    /// <summary>Length indicator low limit; a non-number stores the default (50).</summary>
    public string LowLimitText
    {
        get => _lowLimitText;
        set
        {
            if (SetProperty(ref _lowLimitText, value))
                Settings.Set(AppPreferences.SubjectWarnKey, ParseInt(value, AppPreferences.DefaultSubjectWarnAt));
        }
    }

    /// <summary>Length indicator high limit; a non-number stores the default (70).</summary>
    public string HighLimitText
    {
        get => _highLimitText;
        set
        {
            if (SetProperty(ref _highLimitText, value))
                Settings.Set(AppPreferences.SubjectErrorKey, ParseInt(value, AppPreferences.DefaultSubjectErrorAt));
        }
    }

    static int ParseInt(string text, int fallback) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    // ------------------------------------------------------------------ Git

    /// <summary>`&lt;git version&gt; - &lt;absolute path&gt;`; GerGit always runs this git.</summary>
    [ObservableProperty]
    public partial string GitInstance { get; private set; } = "";

    /// <summary>The global config's own `user.name` (not from included files).</summary>
    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string UserEmail { get; set; } = "";

    /// <summary>Placeholders: the effective value (with `[include]` / `includeIf` files) when there is one, else the
    /// placeholder; seen while the own value is empty.</summary>
    [ObservableProperty]
    public partial string UserNameHint { get; private set; } = "User name";

    [ObservableProperty]
    public partial string UserEmailHint { get; private set; } = "Email";

    /// <summary>E-mail of the avatar: the own value, else the effective one; follows the box when it is written.
    /// </summary>
    [ObservableProperty]
    public partial string AvatarEmail { get; private set; } = "";

    readonly Dictionary<string, string> _savedIdentity = new() { ["user.name"] = "", ["user.email"] = "" };
    string _effectiveEmail = "";

    async Task<string> ReadConfigAsync(string key, bool includes) =>
        (await GitRunner.RunAsync(null, ["config", .. _scope, .. includes ? new[] { "--includes" } : [], key])).Stdout.Trim();

    async Task LoadGitAsync()
    {
        var version = await GitRunner.RunAsync(null, ["--version"]);
        var firstLine = version.Stdout.Split('\n')[0].Trim();
        GitInstance = version.Success ? $"{firstLine} - {OpenIn.FindOnPath(GitRunner.GitPath) ?? GitRunner.GitPath}" : "";
        var name = await ReadConfigAsync("user.name", includes: false);
        var email = await ReadConfigAsync("user.email", includes: false);
        var effectiveName = await ReadConfigAsync("user.name", includes: true);
        _effectiveEmail = await ReadConfigAsync("user.email", includes: true);
        (_savedIdentity["user.name"], _savedIdentity["user.email"]) = (name, email);
        // Text typed while the values were read stays.
        if (UserName.Length == 0) UserName = name;
        if (UserEmail.Length == 0) UserEmail = email;
        UserNameHint = effectiveName.Length > 0 ? effectiveName : "User name";
        UserEmailHint = _effectiveEmail.Length > 0 ? _effectiveEmail : "Email";
        AvatarEmail = email.Length > 0 ? email : _effectiveEmail;
    }

    /// <summary>
    /// A box lost focus (or the window closes): writes `git config --global --replace-all` for each key whose trimmed
    /// value differs from the stored one; a failure shows git's message.
    /// An unchanged key is not written and
    /// an empty box is never written (no `""`, no unset). A global `user.name ""` would override a name that comes from
    /// an `[include]` / `includeIf` file and make every commit fail; clearing a value is done in the config file.
    /// A key counts as stored only after its write succeeded, so a failed write is tried again at the next focus loss or
    /// close. A save asked for while one runs runs once more after it (UI thread only).
    /// </summary>
    public async Task SaveIdentityAsync()
    {
        await Loading;
        if (_saving)
        {
            _saveAgain = true;
            return;
        }
        _saving = true;
        try
        {
            do
            {
                _saveAgain = false;
                if (!await WriteIdentityAsync()) return;
            } while (_saveAgain);
        }
        finally { _saving = false; }
        AvatarEmail = _savedIdentity["user.email"] is { Length: > 0 } own ? own : _effectiveEmail;
    }

    bool _saving, _saveAgain;

    /// <summary>Writes the changed keys; false after a failure (shown).</summary>
    async Task<bool> WriteIdentityAsync()
    {
        foreach (var (key, value) in new[] { ("user.name", UserName.Trim()), ("user.email", UserEmail.Trim()) })
        {
            if (value.Length == 0 || value == _savedIdentity[key]) continue;
            // `--` before the key: a value starting with "-" is never an option. (After the key, git would store "--"
            // itself as the value and take the real value as a value pattern.)
            var r = await GitRunner.RunAsync(null, ["config", .. _scope, "--replace-all", "--", key, value]);
            if (r.Success)
            {
                _savedIdentity[key] = value;
                continue;
            }
            await ShowDialog(new DialogViewModel { Title = "Git Error", Description = r.ReadableOutput, SubmitText = null, CancelText = "Close" });
            return false;
        }
        return true;
    }

    // ------------------------------------------------------------------ Integration

    /// <summary>`Highlight issue tracker links in commit messages`, the master switch; shown
    /// messages are linked again at once.</summary>
    public bool IssueLinks
    {
        get => GerGit.IssueLinks.GlobalEnabled;
        set
        {
            GerGit.IssueLinks.GlobalEnabled = value;
            OnPropertyChanged();
        }
    }

    /// <summary>Off: no avatar is downloaded (authors, accounts, notifications); pictures already shown stay until their
    /// view is filled again.</summary>
    public bool LoadAvatars
    {
        get => AppPreferences.LoadAvatars;
        set { AppPreferences.LoadAvatars = value; OnPropertyChanged(); }
    }

    /// <summary>`External Merge Tools` and `External Diff Tools`.</summary>
    public ExternalToolsEditorViewModel MergeTools { get; }
    public ExternalToolsEditorViewModel DiffTools { get; }

    TerminalSetting _terminal;
    readonly string _detectedTerminal;

    public IReadOnlyList<string> ShellKinds { get; } = ["Default", "Custom"];

    /// <summary>Shell combo: 0 = Default (detected terminal), 1 = Custom.</summary>
    public int ShellIndex
    {
        get => _terminal.IsCustom ? 1 : 0;
        set
        {
            if (value < 0 || value == ShellIndex) return;
            SetTerminal(_terminal with { Kind = value == 1 ? "custom" : "auto" });
        }
    }

    public bool ShellIsCustom => _terminal.IsCustom;

    /// <summary>Default: the detected terminal (empty when none); Custom: the chosen executable.</summary>
    public string ShellPath => _terminal.IsCustom ? _terminal.Path : _detectedTerminal;

    /// <summary>Extra arguments, editable only for Custom.</summary>
    public string ShellArgs
    {
        get => _terminal.IsCustom ? _terminal.Args : "";
        set
        {
            if (_terminal.IsCustom && value != _terminal.Args) SetTerminal(_terminal with { Args = value });
        }
    }

    /// <summary>Browse ("Select shell"), Custom only.</summary>
    public void SetShellPath(string path)
    {
        if (_terminal.IsCustom) SetTerminal(_terminal with { Path = path });
    }

    void SetTerminal(TerminalSetting value)
    {
        _terminal = value;
        AppPreferences.Current.Terminal = value;
        OnPropertyChanged(nameof(ShellIndex));
        OnPropertyChanged(nameof(ShellIsCustom));
        OnPropertyChanged(nameof(ShellPath));
        OnPropertyChanged(nameof(ShellArgs));
    }
}
