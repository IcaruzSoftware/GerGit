using System.Reflection;
using System.Runtime.CompilerServices;
using GerGit.ViewModels;
using Xunit.v3;

[assembly: GerGit.Tests.ResetGlobalSettings]

namespace GerGit.Tests;

static class TestSettings
{
    /// <summary>The settings store of every test writes to a throwaway folder, never the user's real file; avatars are
    /// never downloaded; the OS theme is dark.</summary>
    [ModuleInitializer]
    internal static void UseTempConfigDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gergit-test-config-" + Environment.ProcessId);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); // left by an earlier run with the same process id
        Environment.SetEnvironmentVariable("GERGIT_CONFIG_DIR", dir);
        // git started by GerGit in tests never reads the developer's global config (TempRepo does the same for its own git).
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", "/dev/null");
        GerGit.Hosting.Avatars.Download = (_, _) => Task.FromResult<byte[]?>(null);
        // The developer's own diff / merge tools (meld, code, ...) never show up in menus; tests that need one set it.
        GerGit.ViewModels.ExternalTools.FindBuiltIn = _ => null;
        // The OS "prefers dark" (portal color-scheme 1), so every new App starts dark as the colour checks expect; the
        // real portal and GTK settings are never read.
        GerGit.Views.AppTheme.Portal = _ => Task.FromResult<uint?>(1);
        GerGit.Views.AppTheme.GtkPrefersDark = () => null;
    }
}

/// <summary>Before every test: the "worktrees seen" flag, which opening any repository with linked worktrees turns on, is
/// off again, so menus ("Checkout as Worktree...") do not depend on the tests that ran before.</summary>
[AttributeUsage(AttributeTargets.Assembly)]
sealed class ResetGlobalSettingsAttribute : BeforeAfterTestAttribute
{
    public override void Before(MethodInfo methodUnderTest, IXunitTest test) =>
        Settings.Set<bool?>(RepoTabViewModel.ShowWorktreesKey, null);
}
