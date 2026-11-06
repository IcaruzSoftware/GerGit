using System.Diagnostics;
using Avalonia;
using GerGit.Core;
using GerGit.Core.Git;

namespace GerGit;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Askpass helper mode: started by git/ssh through the SSH_ASKPASS script; no Avalonia at all.
        if (args is ["--askpass", .. var rest]) return Askpass.RunHelper(rest);
        // Interactive-rebase editor helper: started by git as sequence/core editor.
        if (args is ["--rebase-editor", .. var files]) return RebaseEditor.RunHelper(files);
        // A few long jobs must not starve the rest.
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 10), io);
        if (!OperatingSystem.IsWindows())
        {
            // Command line: a running GerGit gets the argument (or "activate") and this
            // start ends here, before any window exists. Any failure: start on our own, without single instance.
            try
            {
                string? request = null; // "activate"
                if (args is [{ Length: > 0 } arg])
                {
                    try { request = Path.GetFullPath(arg); }
                    catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException)
                    {
                        Trace.WriteLine($"Single instance: '{arg}' is no path ({e.Message}), only activating");
                    }
                }
                if ((SingleInstance.Current = SingleInstance.Start(SingleInstance.DefaultSocketPath(), request)) is null) return 0;
            }
            catch (Exception e)
            {
                Trace.WriteLine($"Single instance not available: {e.Message}");
            }
        }
        try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { SingleInstance.Current?.Dispose(); } // normally done on window.Closed already (idempotent)
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();
}
