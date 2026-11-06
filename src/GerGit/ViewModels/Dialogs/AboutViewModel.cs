using System.Diagnostics;
using System.Reflection;

namespace GerGit.ViewModels.Dialogs;

/// <summary>Help > About GerGit: icon, name, version, license and project links. "Copy Version Info" puts the
/// versions and the distribution on the clipboard for issue reports.</summary>
public sealed class AboutViewModel : DialogViewModel
{
    public const string RepositoryUrl = "https://github.com/IcaruzSoftware/GerGit";
    public const string IssueUrl = RepositoryUrl + "/issues/new/choose";
    public const string LicenseUrl = RepositoryUrl + "/blob/main/LICENSE";
    public const string NoticesUrl = RepositoryUrl + "/blob/main/THIRD-PARTY-NOTICES.md";

    public AboutViewModel()
    {
        Title = "About GerGit";
        SubmitText = null;
        SecondaryText = "Copy Version Info";
        CancelText = "Close";
        CancelIsDefault = true;
        PlainFrame = true;
        Width = 440;
    }

    /// <summary>The app version from the assembly, without the "+commit" suffix the SDK appends.</summary>
    public static string Version { get; } = VersionOf(typeof(AboutViewModel).Assembly);

    public static string AvaloniaVersion { get; } = VersionOf(typeof(Avalonia.Application).Assembly);

    public static string DotNetVersion => Environment.Version.ToString();

    public string VersionLine => $"Version {Version}";

    public string RuntimeInfo => $".NET {DotNetVersion} · Avalonia {AvaloniaVersion}";

    /// <summary>The clipboard text: "GerGit x.y.z, .NET x, Avalonia x, &lt;distribution&gt;".</summary>
    public string VersionInfo => $"GerGit {Version}, .NET {DotNetVersion}, Avalonia {AvaloniaVersion}, {Distribution()}";

    /// <summary>Set by the view: writes text to the clipboard.</summary>
    public Func<string, Task>? Copy { get; set; }

    protected override async Task OnSecondaryAsync()
    {
        if (Copy is null) return;
        await Copy(VersionInfo);
        SetStatus(DialogStatus.Success, "Version info copied.");
    }

    static string VersionOf(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3) ?? "";
        return version.Split('+')[0];
    }

    /// <summary>PRETTY_NAME of /etc/os-release, "Linux" when it cannot be read.</summary>
    static string Distribution()
    {
        try { return PrettyName(File.ReadAllText("/etc/os-release")) ?? "Linux"; }
        catch (Exception e)
        {
            Trace.WriteLine($"Cannot read /etc/os-release: {e.Message}");
            return "Linux";
        }
    }

    /// <summary>The PRETTY_NAME value of an os-release text, without its quotes; null when missing or empty.</summary>
    internal static string? PrettyName(string osRelease) =>
        osRelease.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("PRETTY_NAME=", StringComparison.Ordinal))
            .Select(l => l["PRETTY_NAME=".Length..].Trim('"', '\'')).FirstOrDefault(v => v.Length > 0);
}
