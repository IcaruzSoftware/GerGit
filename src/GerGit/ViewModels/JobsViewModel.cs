using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GerGit.Core;

namespace GerGit.ViewModels;

/// <summary>
/// The jobs of one repository tab: the history (ring of the last 100 non-hidden jobs, newest
/// first) and the running set (hidden jobs too). UI thread only.
/// </summary>
public sealed class JobQueue
{
    public const int HistorySize = 100;
    readonly List<Job> _history = [];
    readonly List<Job> _running = [];

    public IReadOnlyList<Job> History => _history;
    public IReadOnlyList<Job> Running => _running;

    /// <summary>No job of this tab runs (the status box spinner is hidden).</summary>
    public bool IsIdle => _running.Count == 0;

    /// <summary>Changes when a non-hidden job is added or ends (the Activity Manager re-syncs its rows only then).</summary>
    public int Version { get; private set; }

    public void Start(Job job)
    {
        if (!job.Hidden)
        {
            _history.Insert(0, job);
            if (_history.Count > HistorySize) _history.RemoveAt(HistorySize);
        }
        _running.Add(job);
        if (!job.Hidden) Version++;
    }

    public void End(Job job)
    {
        _running.Remove(job);
        if (!job.Hidden) Version++;
    }

    /// <summary>
    /// Status box: the newest job that is running, or that finished with a message less than 2 s ago.
    /// Background jobs (automatic fetch) do not "stay in the status box": they count only after 500 ms of running.
    /// </summary>
    public Job? Primary(DateTime utcNow) => _history.FirstOrDefault(j => j.Background
        ? j.IsRunning && utcNow - j.Started > TimeSpan.FromMilliseconds(500)
        : j.IsRunning || (j.Message.Length > 0 && j.Finished is { } f && utcNow - f < TimeSpan.FromSeconds(2)));
}

/// <summary>Text of a job in the status box and the Activity Manager.</summary>
public static class JobTexts
{
    /// <summary>Status-box description: the message, or `Canceling...` / `Canceled` after a cancel.</summary>
    public static string Description(Job job) =>
        !job.CancelRequested ? job.Message : job.IsRunning ? "Canceling..." : "Canceled";

    /// <summary>Progress bar value `5 + p × 0.95`, null = no bar.</summary>
    public static double? ProgressValue(Job job) => job.Progress is { } p ? 5 + p * 0.95 : null;

    /// <summary>Activity Manager state text, e.g. `succeeded 4 Oct 2026 14:03:22` (local time).</summary>
    public static string State(Job job)
    {
        var state = job.Outcome switch
        {
            JobOutcome.Running => job.CancelRequested ? "canceling..." : "running",
            JobOutcome.Canceled => "canceled",
            JobOutcome.Failed => "failed",
            _ => "succeeded",
        };
        return job.Finished is { } f
            ? state + " " + f.ToLocalTime().ToString("d MMM yyyy HH:mm:ss", CultureInfo.InvariantCulture)
            : state;
    }
}

/// <summary>The centre of the toolbar while a primary job exists; updated every 200 ms.</summary>
public sealed partial class StatusBoxViewModel : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasJob))]
    public partial Job? PrimaryJob { get; private set; }

    public bool HasJob => PrimaryJob is not null;

    [ObservableProperty]
    public partial string JobTitle { get; private set; } = "";

    [ObservableProperty]
    public partial string JobDescription { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(ProgressValue))]
    public partial double? Progress { get; private set; }

    public bool HasProgress => Progress is not null;
    public double ProgressValue => Progress ?? 0;

    [ObservableProperty]
    public partial bool CanCancel { get; private set; }

    /// <summary>Any job of the active tab runs, hidden ones (refresh) included: the spinner left of the title.</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; private set; }

    public void Update(RepoTabViewModel? tab, DateTime utcNow)
    {
        var job = tab?.Jobs.Primary(utcNow);
        PrimaryJob = job;
        JobTitle = job?.Name ?? "";
        JobDescription = job is null ? "" : JobTexts.Description(job);
        Progress = job is null ? null : JobTexts.ProgressValue(job);
        CanCancel = job is { IsRunning: true, CancelRequested: false };
        IsBusy = tab is not null && !tab.Jobs.IsIdle;
    }

    [RelayCommand]
    void Cancel() => PrimaryJob?.Cancel();
}

/// <summary>One row of the Activity Manager job list, updated in place so the selection stays.</summary>
public sealed partial class JobRowViewModel(Job job) : ObservableObject
{
    public Job Job { get; } = job;
    public string Name => Job.Name;

    [ObservableProperty]
    public partial string Message { get; private set; } = "";

    /// <summary>`HH:mm` local, once finished.</summary>
    [ObservableProperty]
    public partial string? FinishTime { get; private set; }

    /// <summary>Failed or cancelled: warning icon before the time.</summary>
    [ObservableProperty]
    public partial bool ShowWarning { get; private set; }

    [ObservableProperty]
    public partial bool IsRunning { get; private set; }

    [ObservableProperty]
    public partial bool CanCancel { get; private set; }

    /// <summary>Running jobs with progress only.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProgress), nameof(ProgressValue))]
    public partial double? Progress { get; private set; }

    public bool HasProgress => Progress is not null;
    public double ProgressValue => Progress ?? 0;

    public void Update()
    {
        Message = Job.Message;
        FinishTime = Job.Finished?.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        ShowWarning = Job.Outcome is JobOutcome.Failed or JobOutcome.Canceled;
        IsRunning = Job.IsRunning;
        CanCancel = Job is { IsRunning: true, CancelRequested: false };
        Progress = Job.IsRunning ? JobTexts.ProgressValue(Job) : null;
    }

    [RelayCommand]
    void Cancel() => Job.Cancel();
}

/// <summary>
/// Activity Manager popup: job list of the active tab, filtered by the All / User / Background tabs, and the
/// selected job's output and state.
/// </summary>
public sealed partial class ActivityViewModel : ObservableObject
{
    /// <summary>`activity.filter`: "all" / "user" / "background" (index 0 / 1 / 2 of the tab strip).</summary>
    public const string FilterKey = "activity.filter";
    static readonly string[] Filters = ["all", "user", "background"];

    JobQueue? _queue;
    int _version = -1;

    /// <summary>The selected tab (remembered). Changing it refreshes the list and selects the first row.</summary>
    public int FilterIndex
    {
        get => Math.Max(0, Array.IndexOf(Filters, Settings.Get(FilterKey, "all")));
        set
        {
            if (value == FilterIndex || value < 0 || value >= Filters.Length) return;
            Settings.Set(FilterKey, Filters[value]);
            OnPropertyChanged();
            _version = -1;
            Refresh(_queue);
            Selected = Rows.FirstOrDefault();
        }
    }

    public ObservableCollection<JobRowViewModel> Rows { get; } = [];

    [ObservableProperty]
    public partial bool IsOpen { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    public partial JobRowViewModel? Selected { get; set; }

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    public partial string Output { get; private set; } = "";

    [ObservableProperty]
    public partial string StateText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; } = true;

    partial void OnSelectedChanged(JobRowViewModel? value) => UpdateDetails();

    /// <summary>Opening selects the first row.</summary>
    public void Open(JobQueue? queue)
    {
        Refresh(queue);
        Selected = Rows.FirstOrDefault();
        IsOpen = true;
    }

    /// <summary>Every 200 ms while open: rows re-synced only when the queue changed, then updated in place.</summary>
    public void Refresh(JobQueue? queue)
    {
        if (queue != _queue || queue?.Version != _version)
        {
            _queue = queue;
            _version = queue?.Version ?? -1;
            int filter = FilterIndex;
            var jobs = (queue?.History ?? []).Where(j => filter == 0 || j.Background == (filter == 2));
            var keep = Rows.ToDictionary(r => r.Job);
            var selected = Selected;
            Rows.Clear();
            foreach (var j in jobs) Rows.Add(keep.TryGetValue(j, out var row) ? row : new JobRowViewModel(j));
            Selected = selected is not null && Rows.Contains(selected) ? selected : null;
            IsEmpty = Rows.Count == 0;
        }
        foreach (var r in Rows) r.Update();
        UpdateDetails();
    }

    void UpdateDetails()
    {
        var job = Selected?.Job;
        var output = job?.Output ?? "";
        if (output != Output) Output = output; // replaced only when changed
        StateText = job is null ? "" : JobTexts.State(job);
    }
}
