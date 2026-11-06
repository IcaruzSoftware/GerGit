using Avalonia.Controls;
using GerGit.ViewModels.Dialogs;

namespace GerGit.Views.Dialogs;

public partial class CredentialsView : UserControl
{
    public CredentialsView() => InitializeComponent();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Input.PasswordChar = DataContext is CredentialsViewModel { IsMasked: true } ? '•' : default;
    }
}
