using Avalonia.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Views.Dialogs;

namespace GerGit.ViewModels.Dialogs;

/// <summary>
/// Credentials prompt: one text box (visible for a user name, masked otherwise),
/// OK always enabled; <see cref="Answer"/> = the typed text, or null when cancelled.
/// The "Remember in Credential Manager" box stays hidden; keyring storage of prompt answers is not built yet.
/// </summary>
public sealed partial class CredentialsViewModel : DialogViewModel
{
    public CredentialsViewModel(CredentialPrompt prompt)
    {
        Title = prompt.RepoPath.Length > 0 ? Path.GetFileName(prompt.RepoPath.TrimEnd('/')) : "Credentials Required";
        var text = prompt.Text.Trim();
        (Description, Label) = prompt.Kind switch
        {
            PromptKind.UserName => (text, "User Name:"),
            PromptKind.SshPassphrase => ($"Passphrase for SSH key '{prompt.Target}'", "Passphrase:"),
            PromptKind.OtherPassphrase => (text, "Passphrase:"),
            // A password prompt says Password, not Passphrase.
            PromptKind.SshPassword => ($"Password for '{prompt.Target}'", "Password:"),
            _ => (text, "Password:"),
        };
        IsMasked = prompt.Kind != PromptKind.UserName;
        SubmitText = "OK";
        Width = 520; // 520 wide, height fits (min 170)
        MinHeight = 170;
    }

    public string Label { get; }
    public bool IsMasked { get; }

    [ObservableProperty]
    public partial string Text { get; set; } = "";

    public string? Answer => Result == true ? Text : null;

    /// <summary>Shows the prompt (UI thread) over <paramref name="owner"/> (default: the newest dialog or the main window).</summary>
    public static async Task<string?> AskAsync(CredentialPrompt prompt, Window? owner = null)
    {
        var vm = new CredentialsViewModel(prompt);
        await DialogWindow.ShowAsync(vm, owner);
        return vm.Answer;
    }
}

/// <summary>
/// Unknown SSH host key: ssh's full question with the fingerprint, then Accept Once / Accept Always /
/// Cancel. Cancel is the default button, so Enter accepts nothing.
/// </summary>
public sealed class HostKeyViewModel : DialogViewModel
{
    bool _always;

    public HostKeyViewModel(HostKeyPrompt prompt)
    {
        Title = "Unknown SSH Host";
        Description = "The server's host key is not known yet. Check the fingerprint before you continue.";
        Text = prompt.Text.Trim();
        SecondaryText = "Accept Once";
        SubmitText = "Accept Always";
        CancelIsDefault = true;
        Width = 620;
    }

    public string Text { get; }

    public HostKeyAnswer Answer => Result != true ? HostKeyAnswer.Cancel : _always ? HostKeyAnswer.Always : HostKeyAnswer.Once;

    /// <summary>Shows the question (UI thread) over <paramref name="owner"/> (default: the newest dialog or the main window).</summary>
    public static async Task<HostKeyAnswer> AskAsync(HostKeyPrompt prompt, Window? owner = null)
    {
        var vm = new HostKeyViewModel(prompt);
        await DialogWindow.ShowAsync(vm, owner);
        return vm.Answer;
    }

    protected override Task OnSubmitAsync()
    {
        _always = true;
        Close(true);
        return Task.CompletedTask;
    }

    protected override Task OnSecondaryAsync()
    {
        Close(true);
        return Task.CompletedTask;
    }
}
