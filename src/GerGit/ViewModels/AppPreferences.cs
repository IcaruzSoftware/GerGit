using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core.Git;

namespace GerGit.ViewModels;

/// <summary>`integration.terminal`: kind "auto" (detected terminal) or "custom" (path + arguments). The
/// custom path and arguments are kept while "auto" is chosen, so switching back restores them.</summary>
public sealed record TerminalSetting(
    [property: JsonPropertyName("kind")] string Kind = "auto",
    [property: JsonPropertyName("path")] string Path = "",
    [property: JsonPropertyName("args")] string Args = "")
{
    public bool IsCustom => Kind == "custom";
}

/// <summary>
/// The Preferences values that other parts of the app read, stored in <see cref="Settings"/> at
/// once. Views bind to <see cref="Current"/> for the values that change live in open views (diff font size, terminal
/// name).
/// </summary>
public sealed class AppPreferences : ObservableObject
{
    public static AppPreferences Current { get; } = new();

    public const string EditorFontSizeKey = "general.editorFontSize";
    public const string SubjectWarnKey = "commit.subjectWarnAt", SubjectErrorKey = "commit.subjectErrorAt";
    public const string TerminalKey = "integration.terminal";

    /// <summary>Folders scanned for repositories: absolute paths
    /// with a trailing "/". Read by the Repository Manager.</summary>
    public const string SourceFoldersKey = "repositories.sourceFolders";

    public const double DefaultEditorFontSize = 13;
    public const int DefaultSubjectWarnAt = 50, DefaultSubjectErrorAt = 70;

    /// <summary>Diff font size, 10 to 40.</summary>
    public double EditorFontSize
    {
        get => Math.Clamp(Settings.Get(EditorFontSizeKey, DefaultEditorFontSize), 10, 40);
        set
        {
            Settings.Set(EditorFontSizeKey, Math.Clamp(value, 10, 40));
            OnPropertyChanged();
        }
    }

    /// <summary>Subject length counter limits.</summary>
    public static int SubjectWarnAt => Settings.Get(SubjectWarnKey, DefaultSubjectWarnAt);
    public static int SubjectErrorAt => Settings.Get(SubjectErrorKey, DefaultSubjectErrorAt);

    public TerminalSetting Terminal
    {
        get => Settings.Get(TerminalKey, new TerminalSetting());
        set
        {
            Settings.Set(TerminalKey, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(TerminalName)); // the console button and the menus show the new name
        }
    }

    /// <summary>Settings: the console button caption and the "Open In ..." name; Linux: "Terminal" for the
    /// detected terminal, "Shell" for a custom one.</summary>
    public string TerminalName => Terminal.IsCustom ? "Shell" : "Terminal";

    public const string UpdateSubmodulesKey = "general.updateSubmodules";

    /// <summary>"Update submodules automatically after checkout, pull and merge" (default on); the git engine reads
    /// <see cref="GitRepository.UpdateSubmodulesOnCheckout"/>, set from this at startup and on every change.</summary>
    public static bool UpdateSubmodules
    {
        get => Settings.Get(UpdateSubmodulesKey, true);
        set
        {
            Settings.Set(UpdateSubmodulesKey, value);
            GitRepository.UpdateSubmodulesOnCheckout = value;
        }
    }

    public const string LoadAvatarsKey = "integration.loadAvatars";

    /// <summary>"Load avatars (Gravatar, GitHub, GitLab)" (default on). Covers every avatar download, also the
    /// pictures of accounts and notifications; <see cref="GerGit.Hosting.Avatars.Enabled"/> is set from this at startup
    /// and on every change.</summary>
    public static bool LoadAvatars
    {
        get => Settings.Get(LoadAvatarsKey, true);
        set
        {
            Settings.Set(LoadAvatarsKey, value);
            GerGit.Hosting.Avatars.Enabled = value;
        }
    }

    public static IReadOnlyList<string> SourceFolders
    {
        get => Settings.Get<string[]>(SourceFoldersKey, []);
        set => Settings.Set(SourceFoldersKey, value.ToArray());
    }
}
