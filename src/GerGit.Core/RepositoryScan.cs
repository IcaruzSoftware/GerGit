using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GerGit.Core;

/// <summary>Repository scan: finds ordinary repositories below the source folders, and the validity check of
/// a folder ("valid repository"). Works on the file system only, no git process.</summary>
public static class RepositoryScan
{
    /// <summary>Absolute, no trailing slash (the root stays "/"), with symbolic links resolved like git does (realpath of
    /// the longest existing part, the rest appended), so a path given through a link and the path git reports are the
    /// same key.</summary>
    public static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        if (full.Length > 1) full = full.TrimEnd('/');
        var rest = new List<string>();
        for (var p = full; ;)
        {
            if (RealPath(p) is { } real)
            {
                rest.Reverse();
                return rest.Count == 0 ? real : Path.Combine([real, .. rest]);
            }
            if (Path.GetDirectoryName(p) is not { } parent) return full;
            rest.Add(Path.GetFileName(p));
            p = parent;
        }
    }

    /// <summary>realpath(3): absolute, every link and `.` / `..` resolved by the kernel; null when the path does not
    /// resolve (missing, dangling link, link loop, no access).</summary>
    public static string? RealPath(string path)
    {
        try
        {
            var r = realpath(path, IntPtr.Zero);
            if (r == IntPtr.Zero) return null;
            try { return Marshal.PtrToStringUTF8(r); }
            finally { free(r); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>A regular file, not followed through a symbolic link (no FIFO, device or link); false when unknown.</summary>
    public static bool IsRegularFile(string path)
    {
        const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100, StatxType = 1, ModeOffset = 28;
        var buf = new byte[256]; // struct statx, the same layout on every architecture
        try
        {
            if (statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType, buf) != 0) return false;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        return (BitConverter.ToUInt16(buf, ModeOffset) & 0xF000) == 0x8000; // S_IFMT == S_IFREG
    }

    /// <summary>The owner of a file or folder itself, a link not followed (git's ownership check uses lstat too);
    /// null when unknown.</summary>
    public static uint? OwnerUid(string path)
    {
        const int AtFdCwd = -100, AtSymlinkNoFollow = 0x100, MaskOffset = 0, UidOffset = 20;
        const uint StatxUid = 8;
        var buf = new byte[256]; // struct statx, the same layout on every architecture
        try
        {
            if (statx(AtFdCwd, path, AtSymlinkNoFollow, StatxUid, buf) != 0) return null;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
        return (BitConverter.ToUInt32(buf, MaskOffset) & StatxUid) != 0 ? BitConverter.ToUInt32(buf, UidOffset) : null;
    }

    /// <summary>The effective user id of this process.</summary>
    public static uint CurrentUid() => geteuid();

    /// <summary>The user name of <paramref name="uid"/> (getpwuid_r), `uid N` when it has none.</summary>
    public static string UserName(uint uid)
    {
        const int ERANGE = 34;
        var entry = Marshal.AllocHGlobal(256); // struct passwd
        try
        {
            for (int size = 16 * 1024; size <= 1024 * 1024; size *= 4)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    int err = getpwuid_r(uid, entry, buf, (nuint)size, out var result);
                    if (err == ERANGE) continue;
                    // struct passwd starts with the name pointer (into buf, so read before buf is freed)
                    if (err == 0 && result != IntPtr.Zero && Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(result)) is { Length: > 0 } name)
                        return name;
                    break;
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            Trace.WriteLine($"Cannot look up user {uid}: {e.Message}");
        }
        finally { Marshal.FreeHGlobal(entry); }
        return $"uid {uid}";
    }

    [DllImport("libc")]
    static extern int getpwuid_r(uint uid, IntPtr pwd, IntPtr buf, nuint buflen, out IntPtr result);

    [DllImport("libc")]
    static extern uint geteuid();

    [DllImport("libc", SetLastError = true)]
    static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr resolved);

    [DllImport("libc")]
    static extern void free(IntPtr ptr);

    [DllImport("libc", SetLastError = true)]
    static extern int statx(int dirfd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, byte[] buf);

    /// <summary>Where a `.git` file (`gitdir: &lt;path&gt;`, worktrees and submodules) points, as a full path; null when
    /// <paramref name="dotGit"/> is no regular file (a FIFO or device named .git is never opened) or has no such line.
    /// Read errors throw.</summary>
    public static string? GitFileTarget(string dotGit)
    {
        if (!IsRegularFile(dotGit)) return null;
        var line = File.ReadLines(dotGit).FirstOrDefault()?.Trim() ?? "";
        return line.StartsWith("gitdir:", StringComparison.Ordinal)
            ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dotGit)!, line["gitdir:".Length..].Trim()))
            : null;
    }

    /// <summary>The kind of the repository in <paramref name="folder"/>, null when it is none: `.git` resolves (a folder,
    /// or a file `gitdir: &lt;path&gt;`) to a git dir with `HEAD` and `refs` (an ordinary repository also `objects`). A git
    /// dir with a `commondir` file is a worktree (its `refs` live in the common dir), one under `.git/modules/` a
    /// submodule.</summary>
    public static RepoKind? Validate(string folder)
    {
        try
        {
            var dotGit = Path.Combine(folder, ".git");
            string gitDir;
            if (Directory.Exists(dotGit)) gitDir = dotGit;
            else if (GitFileTarget(dotGit) is { } target) gitDir = target;
            else return null;

            var commonFile = Path.Combine(gitDir, "commondir");
            if (File.Exists(commonFile))
            {
                if (!IsRegularFile(commonFile)) return null;
                var common = Path.GetFullPath(Path.Combine(gitDir, File.ReadLines(commonFile).FirstOrDefault()?.Trim() ?? ""));
                return File.Exists(Path.Combine(gitDir, "HEAD")) && Directory.Exists(Path.Combine(common, "refs")) ? RepoKind.Worktree : null;
            }
            if (!File.Exists(Path.Combine(gitDir, "HEAD")) || !Directory.Exists(Path.Combine(gitDir, "refs"))) return null;
            if (gitDir.Replace('\\', '/').Contains("/.git/modules/", StringComparison.Ordinal)) return RepoKind.Submodule;
            return Directory.Exists(Path.Combine(gitDir, "objects")) ? RepoKind.Repository : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The ordinary repositories below the source folders (each a source folder counts as level 1, so depth 5
    /// finds them up to 4 levels below it). Skips ignored paths (and everything below them), folders starting with "$",
    /// and everything inside a found repository. Worktrees and submodules have a `.git` file and are never found.
    /// Symbolic links are never followed: a link is found only when it is itself a repository (its link path is
    /// returned). Returns the paths as walked below <see cref="Normalize"/>d source folders.
    /// Throws <see cref="OperationCanceledException"/> when cancelled.</summary>
    public static List<string> Scan(IEnumerable<string> sourceFolders, int depth, IEnumerable<string> ignored, CancellationToken ct = default)
    {
        var ignore = ignored.Select(i => i.TrimEnd('/')).Where(i => i.Length > 0).ToList();
        var found = new List<string>();
        // Hidden folders are walked too; a folder that cannot be listed is logged and skipped.
        var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
        foreach (var source in sourceFolders) Walk(Normalize(source), depth);
        return found;

        // An ignored path matches when the folder path starts with it; compared per path component, so ignoring /a/foo keeps /a/foobar.
        bool Ignored(string dir) => ignore.Any(i => dir == i || dir.StartsWith(i + "/", StringComparison.Ordinal));

        void Walk(string dir, int n)
        {
            ct.ThrowIfCancellationRequested();
            if (n <= 0 || Ignored(dir)) return;
            if (Directory.Exists(Path.Combine(dir, ".git")) && Validate(dir) == RepoKind.Repository)
            {
                found.Add(dir);
                return;
            }
            DirectoryInfo[] subs;
            try { subs = new DirectoryInfo(dir).GetDirectories("*", options); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                Trace.WriteLine($"Repository scan: cannot list {dir}: {e.Message}");
                return;
            }
            foreach (var sub in subs.OrderBy(s => s.Name, StringComparer.Ordinal))
            {
                if (sub.Name == ".git" || sub.Name.StartsWith('$')) continue;
                if (sub.LinkTarget is null) Walk(sub.FullName, n - 1);
                else if (n > 1 && !Ignored(sub.FullName) && !Ignored(Normalize(sub.FullName))
                         && Directory.Exists(Path.Combine(sub.FullName, ".git")) && Validate(sub.FullName) == RepoKind.Repository)
                    found.Add(sub.FullName);
            }
        }
    }
}
