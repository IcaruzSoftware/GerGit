using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Hosting;

namespace GerGit.ViewModels.Dialogs.Hosting;

/// <summary>
/// Create Pull Request / Create Merge Request (replaces the plain web page for remotes with a
/// matched account): source branch (read-only), title (default: the tip commit's subject), description, target branch
/// (default: the remote's HEAD branch, else main, else master, else the first). Create runs the REST call while the
/// dialog stays open; success opens the created item in the browser and closes, a failure shows its text in the status
/// line. Closing the dialog abandons a running call.
/// </summary>
public sealed partial class CreatePullRequestViewModel : DialogViewModel
{
    readonly RemoteOps _ops;
    readonly HostedRemote _remote;
    readonly CancellationTokenSource _cts = new();

    public CreatePullRequestViewModel(RemoteOps ops, HostedRemote remote, string source, string tipSha)
    {
        _ops = ops;
        _remote = remote;
        Source = source;
        Title = $"Create {remote.RequestName}";
        Description = $"Create a {remote.RequestName.ToLowerInvariant()} on '{remote.Name}'";
        SubmitText = "Create";
        Width = 560;
        Targets = ops.RemoteBranchesOf(remote.Name).Select(r => ops.NameInRemote(r.FullName)).Order(RemoteOps.Natural).ToList();
        CanSubmit = false;
        CloseRequested += _ => _cts.Cancel();
        Loaded = LoadAsync(tipSha);
    }

    /// <summary>Head / source branch name in the remote.</summary>
    public string Source { get; }

    public IReadOnlyList<string> Targets { get; }

    [ObservableProperty]
    public partial string RequestTitle { get; set; } = "";

    [ObservableProperty]
    public partial string Body { get; set; } = "";

    [ObservableProperty]
    public partial string? SelectedTarget { get; set; }

    /// <summary>The REST call runs: inputs and Create are disabled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditable))]
    public partial bool IsRunning { get; set; }

    public bool IsEditable => !IsRunning;

    /// <summary>Defaults (title, target) are in place.</summary>
    public Task Loaded { get; }

    partial void OnRequestTitleChanged(string value) => Validate();
    partial void OnSelectedTargetChanged(string? value) => Validate();

    void Validate() => CanSubmit = !IsRunning && RequestTitle.Trim().Length > 0 && SelectedTarget is { } t && t != Source;

    async Task LoadAsync(string tipSha)
    {
        string? head = null;
        try
        {
            var headers = await _ops.Tab.Repo.ReadHeadersAsync([tipSha]);
            if (RequestTitle.Length == 0 && headers.Count > 0) RequestTitle = headers[0].Subject;
            head = await _ops.Tab.Repo.ReadRemoteHeadAsync(_remote.Name);
        }
        catch (Exception e)
        {
            // Any failure: the fields stay editable with what is known, and the user sees why (git texts carry no token).
            Trace.WriteLine($"Create {_remote.RequestName}: defaults not read: {e}");
            SetStatus(DialogStatus.Warning, e.Message);
        }
        SelectedTarget ??= new[] { head, "main", "master" }.FirstOrDefault(t => t is not null && Targets.Contains(t)) ?? Targets.FirstOrDefault();
    }

    protected override async Task OnSubmitAsync()
    {
        IsRunning = true;
        Validate();
        SetStatus(DialogStatus.InProgress, $"Creating {_remote.RequestName.ToLowerInvariant()}...");
        try
        {
            var hosting = _ops.Tab.Hosting;
            var client = await hosting.ClientAsync(_remote.Account!);
            var created = await client.CreatePullRequestAsync(_remote.Url!.Slug, Source, SelectedTarget!, RequestTitle.Trim(), Body, _cts.Token);
            if (Result is not null) return; // closed meanwhile
            hosting.OpenBrowser(created.WebUrl);
            Close(true);
        }
        catch (OperationCanceledException) { }
        catch (HostingException e)
        {
            // "Error mapping" texts; they never contain the token.
            SetStatus(DialogStatus.Error, e.Message);
        }
        finally
        {
            IsRunning = false;
            Validate();
        }
    }
}
