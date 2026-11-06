using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;

namespace GerGit.Views;

/// <summary>Modal message with a title, a description, an optional extra button and "Close".</summary>
public sealed class MessageWindow : Window
{
    static bool s_open;

    /// <summary>
    /// Set when a dialog closes; the main window consumes it and skips the next activation refresh
    /// so a refresh error cannot reopen its own error window.
    /// </summary>
    public static bool DialogClosed { get; set; }

    /// <summary>Shows the message over the main window; returns true when the extra button was clicked.
    /// Confirmations pass "Cancel" as <paramref name="closeText"/> and their dialog width.</summary>
    public static Task<bool> ShowAsync(string title, string text, string? extraButton = null, string closeText = "Close", double width = 480)
    {
        var owner = (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not { IsVisible: true })
        {
            Trace.WriteLine($"{title}: {text}"); // headless tests, or before the window exists
            return Task.FromResult(false);
        }
        return ShowAsync(owner, title, text, extraButton, closeText, width);
    }

    public static async Task<bool> ShowAsync(Window owner, string title, string text, string? extraButton = null,
        string closeText = "Close", double width = 480)
    {
        if (s_open)
        {
            Trace.WriteLine($"{title}: {text}"); // never stack a second message window
            return false;
        }
        bool extraClicked = false;
        var w = new MessageWindow
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var close = new Button { Content = closeText, IsDefault = true, IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => w.Close();
        var body = new StackPanel { Margin = new(16), Spacing = 8 };
        body.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.Medium, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 300,
            Content = new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Classes = { "description" } },
        });
        if (extraButton is not null)
        {
            var extra = new Button { Content = extraButton };
            extra.Click += (_, _) => { extraClicked = true; w.Close(); };
            body.Children.Add(extra);
        }
        body.Children.Add(close);
        w.Content = body;
        w.Opened += (_, _) => close.Focus();
        // Set in Closed, not after the await: the owner's Activated can fire before the await resumes.
        w.Closed += (_, _) => { s_open = false; DialogClosed = true; };
        s_open = true;
        try { await w.ShowDialog(owner); }
        finally { s_open = false; } // also when ShowDialog throws before the window opened
        return extraClicked;
    }
}
