using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels.Dialogs;
using GerGit.Views.Dialogs;

namespace GerGit.ViewModels.Preferences;

/// <summary>A rule in the Issue Tracker page's list and editor.</summary>
public sealed partial class IssueRuleItem : ObservableObject
{
    public const string DefaultTestMessage = "Fix documentation of the release process (#12337)";
    public const string DefaultName = "Issue Tracker Rule";

    /// <summary>The preview pattern may block the UI thread at most this long per keystroke.</summary>
    static readonly TimeSpan PreviewTimeout = TimeSpan.FromMilliseconds(50);

    System.Text.RegularExpressions.Regex? _compiled;
    string? _compiledFor;

    readonly string _loadedName;

    public IssueRuleItem(IssueRule rule, string testMessage = DefaultTestMessage)
    {
        Name = _loadedName = rule.Name;
        IsShared = rule.Level == IssueRuleLevel.Shared;
        Regex = rule.Regex;
        Url = rule.Url;
        TestMessage = testMessage;
    }

    [ObservableProperty]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocal), nameof(LevelText), nameof(FileText))]
    public partial bool IsShared { get; set; }

    /// <summary>The second radio button.</summary>
    public bool IsLocal
    {
        get => !IsShared;
        set => IsShared = !value;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(IsInvalid), nameof(Preview))]
    public partial string Regex { get; set; }

    /// <summary>Compiled lazily, once per pattern change (NonBacktracking when possible, else a 50 ms timeout): only the
    /// selected rule's editor asks, so opening the window compiles nothing for the other rules.</summary>
    System.Text.RegularExpressions.Regex? Compiled
    {
        get
        {
            if (_compiledFor != Regex) (_compiled, _compiledFor) = (IssueTracker.Compile(Regex, log: false, PreviewTimeout), Regex);
            return _compiled;
        }
    }

    /// <summary>Whether the pattern was compiled (tests).</summary>
    internal bool IsCompiled => _compiledFor == Regex;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    public partial string Url { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    public partial string TestMessage { get; set; }

    public string LevelText => IsShared ? "Shared" : "Local";
    public string FileText => IsShared ? ".issuetracker" : ".git/issuetracker";

    /// <summary>`valid` / `invalid` status: an empty pattern or one that does not compile is invalid.</summary>
    public bool IsValid => Compiled is not null;
    public bool IsInvalid => !IsValid;

    /// <summary>The test message with this rule applied (only http(s) results are links).</summary>
    public LinkedText Preview => new(TestMessage, Compiled is { } regex ? IssueTracker.Find(TestMessage, [(regex, Url)], plainUrls: false) : []);

    /// <summary>The rule as stored; an edited name that is empty gets <see cref="DefaultName"/> (a rule without a name is
    /// not read back). A loaded name is kept as it is until the user edits it, so opening and closing never rewrites a
    /// file.</summary>
    public IssueRule ToRule() =>
        new(Name == _loadedName || Name.Trim().Length > 0 ? Name : DefaultName, IsShared ? IssueRuleLevel.Shared : IssueRuleLevel.Local, Regex, Url);
}

/// <summary>
/// The Repository Settings window of one repository: General (repository git identity) and Issue Tracker.
/// The issue link switch is stored at once; the identity and the rules are pending edits, written by
/// <see cref="SaveAsync"/> when the window closes in any way (<see cref="OpenAsync"/>).
/// Only the settings GerGit has features for are shown. Not built yet: a main branch and no-fast-forward setting,
/// diff view tab width (the diff view uses 4), the Commit page (commit template and Signed-off-by) and the Custom
/// Commands page.
/// </summary>
public sealed partial class RepositorySettingsViewModel : DialogViewModel
{
    readonly RepoInfo _info;
    readonly Dictionary<IssueRuleLevel, IssueRuleFile> _loadedRules = [];

    /// <summary>Rules beyond <see cref="IssueLinks.MaxRules"/> (hostile or huge files): not listed, kept unchanged on save.</summary>
    readonly List<IssueRule> _hiddenRules = [];

    public RepositorySettingsViewModel(RepoInfo info, string repositoryName)
    {
        Title = "Repository Settings";
        SubmitText = null;
        CancelText = "Close";
        CancelIsDefault = true; // Close is the default button (Enter) and Esc closes too
        PlainFrame = true;
        Width = 640; // the widest page (Issue Tracker, 600) + 40
        _info = info;
        RepositoryName = repositoryName;
        GlobalIssueLinks = IssueLinks.GlobalEnabled;
        _repoIssueLinks = IssueLinks.RepoEnabled(info);
        foreach (var level in new[] { IssueRuleLevel.Shared, IssueRuleLevel.Local })
        {
            _loadedRules[level] = IssueTracker.ReadFile(info, level);
            foreach (var r in _loadedRules[level].Rules)
                if (Rules.Count < IssueLinks.MaxRules) Rules.Add(new(r));
                else _hiddenRules.Add(r);
        }
        SelectedRule = Rules.FirstOrDefault();
        Loading = LoadIdentityAsync();
    }

    /// <summary>Settings for This Repository...; afterwards the shown messages
    /// are linked again and the tab refreshes.</summary>
    public static async Task OpenAsync(RepoTabViewModel tab)
    {
        var vm = new RepositorySettingsViewModel(tab.Repo.Info, tab.Title) { ShowDialog = tab.ShowDialog };
        await tab.ShowDialog(vm);
        await vm.SaveAsync();
        IssueLinks.RaiseChanged();
        await tab.RefreshAsync();
    }

    /// <summary>Confirmations and errors over this window; tests answer them.</summary>
    internal Func<DialogViewModel, Task<bool>> ShowDialog { get; set; } = vm => DialogWindow.ShowAsync(vm);

    public string RepositoryName { get; }

    /// <summary>The identity read on open (tests await it).</summary>
    public Task Loading { get; }

    /// <summary>Every identity git call runs alone (the read on open and each save), so nothing races.</summary>
    readonly SemaphoreSlim _git = new(1, 1);

    /// <summary>Writes the pending edits: the identity (removing the repository's own one only after a confirmation),
    /// then the rule files whose rules changed.</summary>
    public async Task SaveAsync()
    {
        await Loading;
        await _git.WaitAsync();
        try { await SaveIdentityAsync(); }
        finally { _git.Release(); }
        SaveRules();
    }

    // ------------------------------------------------------------------ General

    // The identity as read: the repository's own values (null = not set there) and the effective global ones.
    string? _localName, _localEmail;
    string _globalName = "", _globalEmail = "";

    // The boxes' own values, kept while the check box shows the global identity, so unticking it restores them.
    string _ownName = "", _ownEmail = "";

    /// <summary>`Use global git credentials`: checked when neither value comes from the repository's own config. A
    /// pending edit: ticking shows the global values (read-only), unticking brings back the repository's own values.</summary>
    public bool UseGlobalIdentity
    {
        get;
        set
        {
            if (field == value) return;
            if (value) (_ownName, _ownEmail) = (UserName, UserEmail);
            field = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IdentityEditable));
            if (value) (UserName, UserEmail) = (_globalName, _globalEmail);
            else (UserName, UserEmail) = (Or(_ownName, _globalName), Or(_ownEmail, _globalEmail));
        }
    } = true;

    static string Or(string a, string b) => a.Length > 0 ? a : b;

    /// <summary>Disabled when name and e-mail come from different scopes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IdentityEditable))]
    public partial bool CanToggleIdentity { get; private set; } = true;

    public bool IdentityEditable => CanToggleIdentity && !UseGlobalIdentity;

    [ObservableProperty]
    public partial string UserName { get; set; } = "";

    [ObservableProperty]
    public partial string UserEmail { get; set; } = "";

    /// <summary>2 "Identity behaviour": `git config --show-scope --get-regexp`; the last entry of a key per
    /// side wins, scopes `local` and `worktree` are the repository's own, anything else counts as global.</summary>
    async Task LoadIdentityAsync()
    {
        await _git.WaitAsync();
        try
        {
            foreach (var (scope, key, value) in await ReadIdentityAsync())
            {
                bool local = scope is "local" or "worktree"; // both belong to this repository (as in CheckIdentityRemovedAsync)
                if (key == "user.name") { if (local) _localName = value; else _globalName = value; }
                else if (key == "user.email") { if (local) _localEmail = value; else _globalEmail = value; }
            }
            (_ownName, _ownEmail) = (_localName ?? "", _localEmail ?? "");
            bool name = _localName is not null, email = _localEmail is not null;
            CanToggleIdentity = name == email;
            if (name || email) UseGlobalIdentity = false; // shows the own values
            else (UserName, UserEmail) = (_globalName, _globalEmail);
        }
        finally { _git.Release(); }
    }

    /// <summary>The `user.name` / `user.email` entries of every scope, in git's order (later ones win).</summary>
    async Task<List<(string Scope, string Key, string Value)>> ReadIdentityAsync()
    {
        var r = await GitRunner.RunAsync(_info.WorkTree, ["config", "--show-scope", "--get-regexp", @"^user\.(name|email)$"]);
        var entries = new List<(string, string, string)>();
        foreach (var line in r.Stdout.Split('\n'))
        {
            var tab = line.IndexOf('\t');
            if (tab < 0) continue;
            var rest = line[(tab + 1)..];
            var space = rest.IndexOf(' ');
            entries.Add((line[..tab], (space < 0 ? rest : rest[..space]).ToLowerInvariant(), space < 0 ? "" : rest[(space + 1)..]));
        }
        return entries;
    }

    async Task SaveIdentityAsync()
    {
        if (!CanToggleIdentity) return; // mixed scopes: nothing can be edited
        if (!UseGlobalIdentity)
        {
            await WriteIdentityAsync();
            return;
        }
        if (_localName is null && _localEmail is null) return;
        var global = _globalName.Length + _globalEmail.Length > 0 ? $"'{_globalName} <{_globalEmail}>'" : "none (no global identity is set)";
        var confirm = new DialogViewModel
        {
            Title = "Remove this repository's own identity?",
            Description = $"user.name '{_localName}' and user.email '{_localEmail}' are removed from this repository's config. "
                + $"Commits then use the global identity: {global}.",
            SubmitText = "Remove",
            Width = 580,
        };
        if (!await ShowDialog(confirm)) return; // Cancel keeps the repository's own identity
        foreach (var key in new[] { "user.name", "user.email" })
        {
            var r = await GitRunner.RunAsync(_info.WorkTree, ["config", "--local", "--unset-all", "--", key]);
            if (!r.Success && r.ExitCode != 5) // 5: the key was not set
            {
                await ShowError(r.ReadableOutput);
                return;
            }
        }
        Trace.WriteLine($"Removed the own identity of {_info.WorkTree}: user.name '{_localName}', user.email '{_localEmail}'");
        (_localName, _localEmail) = (null, null);
        await CheckIdentityRemovedAsync();
    }

    /// <summary>
    /// After the removal: `--local --unset-all` does not reach a value from an `include` in the local config or from the
    /// worktree config (`extensions.worktreeConfig`). When the effective name or e-mail still comes from such a scope,
    /// that is said (dialog and log) and the box goes back to unticked with those values, instead of claiming the global
    /// identity applies.
    /// </summary>
    async Task CheckIdentityRemovedAsync()
    {
        var last = new Dictionary<string, (string Scope, string Value)>();
        foreach (var (scope, key, value) in await ReadIdentityAsync()) last[key] = (scope, value);
        var left = last.Where(kv => kv.Value.Scope is "local" or "worktree").ToList();
        if (left.Count == 0) return;
        var text = string.Join(", ", left.Select(kv => $"{kv.Key} '{kv.Value.Value}' ({kv.Value.Scope} config)"));
        Trace.WriteLine($"The own identity of {_info.WorkTree} is still set: {text}");
        foreach (var (key, (_, value)) in left)
            if (key == "user.name") _localName = value;
            else _localEmail = value;
        (_ownName, _ownEmail) = (_localName ?? "", _localEmail ?? "");
        UseGlobalIdentity = false;
        await ShowDialog(new DialogViewModel
        {
            Title = "This repository still has its own identity",
            Description = $"{text} could not be removed with `git config --local` (it comes from an included file or the "
                + "worktree config). Edit that file to remove it.",
            SubmitText = null,
            CancelText = "Close",
            Width = 580,
        });
    }

    /// <summary>`git config --local` for each trimmed box value that differs from the stored one. An empty box is never
    /// written (an empty `user.name` would override the global one and make every commit fail); `--` keeps a value
    /// starting with "-" literal.</summary>
    async Task WriteIdentityAsync()
    {
        foreach (var (key, value) in new[] { ("user.name", UserName.Trim()), ("user.email", UserEmail.Trim()) })
        {
            if (value.Length == 0 || value == (key == "user.name" ? _localName : _localEmail)) continue;
            var r = await GitRunner.RunAsync(_info.WorkTree, ["config", "--local", "--replace-all", "--", key, value]);
            if (!r.Success)
            {
                await ShowError(r.ReadableOutput);
                return;
            }
            if (key == "user.name") _localName = value;
            else _localEmail = value;
        }
    }

    Task ShowError(string text) =>
        ShowDialog(new DialogViewModel { Title = "Git Error", Description = text, SubmitText = null, CancelText = "Close" });

    // ------------------------------------------------------------------ Issue Tracker

    /// <summary>The global preference; off shows the "disabled" panel instead of the page.</summary>
    public bool GlobalIssueLinks { get; }

    bool _repoIssueLinks;

    /// <summary>`Add hyperlinks to issue numbers in commit messages` (repository key `issueLinks`), stored at once.</summary>
    public bool RepoIssueLinks
    {
        get => _repoIssueLinks;
        set
        {
            if (!SetProperty(ref _repoIssueLinks, value)) return;
            RepoSettings.Set(_info.CommonDir, IssueLinks.RepoKey, value ? null : (bool?)false); // the default is not stored
            IssueLinks.RaiseChanged();
            OnPropertyChanged(nameof(ShowRepoDisabled));
            OnPropertyChanged(nameof(ShowRules));
        }
    }

    public bool ShowRepoDisabled => GlobalIssueLinks && !RepoIssueLinks;
    public bool ShowRules => GlobalIssueLinks && RepoIssueLinks;
    public string RepoDisabledText => $"Issue tracker integration for '{RepositoryName}' is disabled";

    public ObservableCollection<IssueRuleItem> Rules { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedRule))]
    public partial IssueRuleItem? SelectedRule { get; set; }

    public bool HasSelectedRule => SelectedRule is not null;

    /// <summary>The `+` menu; the new rule is local, added at the end and selected.</summary>
    public void AddRule(string kind)
    {
        var item = kind switch
        {
            "github" => new IssueRuleItem(new("Sample GitHub Rule", IssueRuleLevel.Local, @"#(\d+)", "https://github.com/username/repository/issues/$1")),
            "jira" => new IssueRuleItem(new("Sample Jira Rule", IssueRuleLevel.Local, @"PROJ-(\d+)", "https://jira.yourcompany.com/browse/PROJ-$1"),
                "PROJ-28 Fix documentation of the build process"),
            "jiraMulti" => new IssueRuleItem(new("Sample Jira Multiproject Rule", IssueRuleLevel.Local, @"(PRJ1|PRJ2)-(\d+)", "https://jira.yourcompany.com/browse/$1-$2"),
                "PRJ1-28 Fix documentation of the build process (PRJ2-45)"),
            _ => new IssueRuleItem(new(IssueRuleItem.DefaultName, IssueRuleLevel.Local, "", "")),
        };
        Rules.Add(item);
        SelectedRule = item;
    }

    /// <summary>`-` after the confirmation; then the previous rule (or the first) is selected.</summary>
    public async Task RemoveRuleAsync()
    {
        if (SelectedRule is not { } item) return;
        var confirm = new DialogViewModel
        {
            Title = "Do you want to remove the selected issue tracker rule?",
            Description = "You can't undo this action",
            SubmitText = "Remove",
            Width = 580,
        };
        if (!await ShowDialog(confirm)) return;
        int index = Rules.IndexOf(item);
        Rules.Remove(item);
        SelectedRule = Rules.Count == 0 ? null : Rules[Math.Max(0, index - 1)];
    }

    /// <summary>Rewrites only the files whose rules changed, so an untouched shared file is never reformatted. A file that
    /// was not read completely (parse error, size limit) is kept as `&lt;file&gt;.bak` before it is replaced.</summary>
    void SaveRules()
    {
        foreach (var level in new[] { IssueRuleLevel.Shared, IssueRuleLevel.Local })
        {
            var now = Rules.Select(r => r.ToRule()).Concat(_hiddenRules).Where(r => r.Level == level).ToList();
            var loaded = _loadedRules[level];
            if (now.SequenceEqual(loaded.Rules)) continue;
            try
            {
                IssueTracker.Write(_info, level, now, keepBackup: !loaded.Complete);
                _loadedRules[level] = new(now, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Trace.WriteLine($"Cannot write the issue tracker rules: {e.Message}");
                ShowDialog(new DialogViewModel
                {
                    Title = "Error", Description = $"Cannot save the issue tracker rules: {e.Message}", SubmitText = null, CancelText = "Close",
                }).Forget();
            }
        }
    }
}
