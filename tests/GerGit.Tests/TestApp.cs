using Avalonia;
using Avalonia.Headless;

[assembly: AvaloniaTestApplication(typeof(GerGit.Tests.TestAppBuilder))]

namespace GerGit.Tests;

public static class TestAppBuilder
{
    // Real Skia rendering and the bundled Inter font, so text measures like in the app and frames can be captured.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<GerGit.App>()
            .UseSkia()
            .WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
