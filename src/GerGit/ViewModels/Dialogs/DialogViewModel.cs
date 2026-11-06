using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;

namespace GerGit.ViewModels.Dialogs;

/// <summary>Footer status of a dialog. InProgress shows the spinner (also shown while <see cref="DialogViewModel.IsBusy"/>).</summary>
public enum DialogStatus { None, InProgress, Warning, Error, Success }

/// <summary>
/// One dialog in the standard frame, hosted by
/// <see cref="Views.Dialogs.DialogWindow"/>. The body view is found by name: <c>GerGit.ViewModels.Dialogs.FooViewModel</c>
/// → <c>GerGit.Views.Dialogs.FooView</c> (DataContext = the view model); a plain <see cref="DialogViewModel"/> has no
/// body and is a message box / confirmation.
/// Submit: <see cref="OnSubmitAsync"/> decides. The default closes with true. A job dialog that closes at once
/// calls <see cref="Close"/> and then starts its job; a dialog that stays open passes itself to
/// <see cref="RepoTabViewModel.RunJobAsync"/>, which keeps it busy and closes it when the job ends.
/// </summary>
public partial class DialogViewModel : ObservableObject
{
    [ObservableProperty]
    public partial string Title { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    public partial string Description { get; set; } = "";

    public bool HasDescription => Description.Length > 0;

    /// <summary>Null hides the Submit button (e.g. the error window).</summary>
    [ObservableProperty]
    public partial string? SubmitText { get; set; } = "Submit";

    /// <summary>A second submit-like button left of Submit (null = hidden); runs <see cref="OnSecondaryAsync"/>.</summary>
    [ObservableProperty]
    public partial string? SecondaryText { get; set; }

    [ObservableProperty]
    public partial string CancelText { get; set; } = "Cancel";

    /// <summary>The first text box of the body gets the focus with its text selected, so typing replaces a prefilled
    /// name. False: focus only.</summary>
    public bool SelectTextOnOpen { get; init; } = true;

    /// <summary>No logo column and no header: the body
    /// draws its own heading; <see cref="Title"/> is only the title bar.</summary>
    public bool PlainFrame { get; init; }

    /// <summary>False: shown without blocking its owner (the main window), e.g. the commit checks window, which can stay
    /// open for minutes while the user keeps browsing. Such a window is never the owner of later dialogs.</summary>
    public bool Modal { get; init; } = true;

    /// <summary>Enter presses Cancel instead of Submit (the host-key question: nothing is accepted by Enter).</summary>
    public bool CancelIsDefault { get; init; }

    /// <summary>The dialog's validity rule; Submit is enabled only when true and not busy.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubmitEnabled))]
    public partial bool CanSubmit { get; set; } = true;

    /// <summary>An operation runs: inputs and Submit disabled, spinner shown; Cancel / Esc / closing the window cancel
    /// <see cref="RunningJob"/> instead of closing.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SubmitEnabled), nameof(IsIdle), nameof(ShowSpinner), nameof(BodyEnabled))]
    public partial bool IsBusy { get; set; }

    public bool IsIdle => !IsBusy;

    /// <summary>The body takes input: not busy and not showing a finished job's result (<see cref="KeepOpen"/>).</summary>
    public bool BodyEnabled => !IsBusy && !KeepOpen;

    /// <summary>The job started by <see cref="RepoTabViewModel.RunJobAsync"/> for this dialog while it runs.</summary>
    public Job? RunningJob { get; set; }
    public bool SubmitEnabled => CanSubmit && !IsBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSpinner), nameof(StatusIcon))]
    public partial DialogStatus Status { get; set; }

    /// <summary>Footer message (one line, trimmed, full text as tooltip).</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = "";

    public bool ShowSpinner => IsBusy || Status == DialogStatus.InProgress;

    /// <summary>Lucide icon of the footer status, null = none.</summary>
    public string? StatusIcon => Status switch
    {
        DialogStatus.Warning => "triangle-alert",
        DialogStatus.Error => "circle-x",
        DialogStatus.Success => "circle-check",
        _ => null,
    };

    /// <summary>24 px warning icon over the logo (destructive confirmations, error window).</summary>
    [ObservableProperty]
    public partial bool WarningBadge { get; set; }

    /// <summary>Fixed width; height fits the content unless <see cref="Height"/> is set.</summary>
    public double Width { get; init; } = 490;
    public double Height { get; init; } = double.NaN;
    public bool CanResize { get; init; }
    public double MinWidth { get; init; }
    public double MinHeight { get; init; }

    /// <summary>A view model shown below the buttons (its view found by the body's naming rule), null = none. Unlike the
    /// body it stays enabled while busy. The Merge dialog's checks panel.</summary>
    public object? Panel { get; init; }

    /// <summary>Set by a job whose result this dialog shows itself (the merge checks):
    /// <see cref="RepoTabViewModel.RunJobAsync"/> then leaves it open and opens no error window.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BodyEnabled))]
    public partial bool KeepOpen { get; set; }

    /// <summary>Null while open; true = closed by submit, false = cancelled.</summary>
    public bool? Result { get; private set; }

    /// <summary>The window listens and closes itself.</summary>
    public event Action<bool>? CloseRequested;

    /// <summary>Shows a footer status line; <see cref="DialogStatus.None"/> clears it.</summary>
    public void SetStatus(DialogStatus status, string text = "")
    {
        Status = status;
        StatusText = text;
    }

    /// <summary>Validity rule with an optional footer warning (e.g. a <see cref="Core.RefName"/> message).</summary>
    public void SetValid(bool valid, string? warning = null)
    {
        CanSubmit = valid;
        SetStatus(warning is null ? DialogStatus.None : DialogStatus.Warning, warning ?? "");
    }

    public void Close(bool result)
    {
        if (Result is not null) return;
        Result = result;
        CloseRequested?.Invoke(result);
    }

    /// <summary>Submit button / Enter. Does nothing while disabled.</summary>
    public async Task SubmitAsync()
    {
        if (SubmitEnabled) await OnSubmitAsync();
    }

    public async Task SecondaryAsync()
    {
        if (SubmitEnabled) await OnSecondaryAsync();
    }

    /// <summary>Cancel button / Esc / the window's close button: closes without result, or while busy cancels the
    /// running job (the job's end then closes the dialog as success).</summary>
    public void Cancel()
    {
        if (!IsBusy) Close(false);
        else RunningJob?.Cancel();
    }

    protected virtual Task OnSubmitAsync()
    {
        Close(true);
        return Task.CompletedTask;
    }

    protected virtual Task OnSecondaryAsync() => Task.CompletedTask;
}
