using CommunityToolkit.Mvvm.ComponentModel;

namespace GerGit.ViewModels.Dialogs;

/// <summary>Go To Line (Blame window only): 250 x 120, no logo or header, `Line number:` box, `Go`.</summary>
public sealed partial class GoToLineViewModel : DialogViewModel
{
    public GoToLineViewModel()
    {
        Title = "Go To Line";
        SubmitText = "Go";
        PlainFrame = true;
        Width = 250;
        Height = 120;
    }

    [ObservableProperty] public partial string Text { get; set; } = "";

    /// <summary>The typed line number; null when the text is not an integer.</summary>
    public int? Line => int.TryParse(Text.Trim(), out var n) ? n : null;
}
