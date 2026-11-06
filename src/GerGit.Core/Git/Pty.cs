using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace GerGit.Core.Git;

/// <summary>
/// A pseudo-terminal for one git command (the Merge dialog's merge): the slave becomes git's controlling terminal, so
/// hooks that write to /dev/tty (such as a test runner's progress) can be read from the master. Linux only (posix_openpt).
/// Both ends are opened close-on-exec; the slave is opened here too, so reading the master never ends before git has
/// opened it (the master reports EIO once every slave descriptor is closed). The terminal is non-canonical with MIN=0
/// and TIME=0: a read of /dev/tty returns EOF at once instead of waiting for input no one can type; output is unchanged.
/// </summary>
sealed class Pty : IDisposable
{
    const int O_RDWR = 2, O_NOCTTY = 0x100, O_NONBLOCK = 0x800, O_CLOEXEC = 0x80000;
    const int EAGAIN = 11, EINTR = 4;

    // struct termios of Linux glibc (x86-64 and arm64): c_iflag, c_oflag, c_cflag, c_lflag (4 × uint32), c_line (byte),
    // c_cc[32], then c_ispeed, c_ospeed: 60 bytes. Handled as raw bytes (no marshalled struct layout).
    const int TermiosSize = 60, LflagOffset = 12, CcOffset = 17, VTIME = 5, VMIN = 6, ICANON = 2, TCSANOW = 0;

    int _master, _slave;

    Pty(int master, int slave, string slavePath)
    {
        _master = master;
        _slave = slave;
        SlavePath = slavePath;
    }

    public string SlavePath { get; }

    /// <summary>A new pty, or null (logged) when none can be allocated or set up; no descriptor is left open then.</summary>
    public static Pty? Open()
    {
        int master = -1, slave = -1;
        try
        {
            // The termios layout above is only right for these two; elsewhere run without a pty.
            if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
                throw new PlatformNotSupportedException("termios layout unknown for " + RuntimeInformation.ProcessArchitecture);
            master = posix_openpt(O_RDWR | O_NOCTTY | O_NONBLOCK | O_CLOEXEC);
            if (master < 0 || grantpt(master) != 0 || unlockpt(master) != 0) throw Error("posix_openpt");
            var name = new byte[128];
            if (ptsname_r(master, name, name.Length) != 0) throw Error("ptsname_r");
            int length = Array.IndexOf(name, (byte)0);
            var path = Encoding.ASCII.GetString(name, 0, length < 0 ? name.Length : length);
            slave = open(path, O_RDWR | O_NOCTTY | O_CLOEXEC);
            if (slave < 0) throw Error("open " + path);
            var termios = new byte[TermiosSize + 64]; // spare room in case the C library's struct is larger
            if (tcgetattr(slave, termios) != 0) throw Error("tcgetattr");
            int lflag = BitConverter.ToInt32(termios, LflagOffset) & ~ICANON;
            BitConverter.TryWriteBytes(termios.AsSpan(LflagOffset), lflag);
            termios[CcOffset + VMIN] = 0;
            termios[CcOffset + VTIME] = 0;
            if (tcsetattr(slave, TCSANOW, termios) != 0) throw Error("tcsetattr");
            return new Pty(master, slave, path);
        }
        catch (Exception e)
        {
            Trace.WriteLine($"No pseudo-terminal, the command runs without one: {e.Message}");
            if (slave >= 0) close(slave);
            if (master >= 0) close(master);
            return null;
        }
    }

    static IOException Error(string what) => new($"{what} failed (errno {Marshal.GetLastPInvokeError()})");

    /// <summary>Reads the master until every slave descriptor is closed (EIO), or after <paramref name="stop"/> one more
    /// read (a writer that is left over cannot keep it going); gives the decoded text to <paramref name="text"/>. Polls
    /// (non-blocking reads), so it never blocks a thread.</summary>
    public Task ReadAsync(Action<string> text, CancellationToken stop) => Task.Run(async () =>
    {
        var decoder = new UTF8Encoding(false, false).GetDecoder();
        var bytes = new byte[4096];
        var chars = new char[4096 + 4];
        while (true)
        {
            bool last = stop.IsCancellationRequested;
            int n = (int)read(_master, bytes, bytes.Length);
            if (n > 0)
            {
                int c = decoder.GetChars(bytes, 0, n, chars, 0);
                if (c > 0) text(new string(chars, 0, c));
                if (last) return;
                continue;
            }
            int errno = n < 0 ? Marshal.GetLastPInvokeError() : 0;
            if (n < 0 && errno == EINTR && !last) continue;
            if (n == 0 || errno != EAGAIN || last) return; // EIO: all slaves closed
            try { await Task.Delay(30, stop); }
            catch (OperationCanceledException) { } // one more read drains what is left
        }
    });

    /// <summary>Our slave descriptor: closed once git has exited, so the master reports EIO when the tree is gone.</summary>
    public void CloseSlave()
    {
        if (_slave >= 0) close(_slave);
        _slave = -1;
    }

    public void Dispose()
    {
        CloseSlave();
        if (_master >= 0) close(_master);
        _master = -1;
    }

    [DllImport("libc", SetLastError = true)]
    static extern int posix_openpt(int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int grantpt(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern int unlockpt(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern int ptsname_r(int fd, byte[] buf, nint len);

    [DllImport("libc", SetLastError = true)]
    static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    static extern int close(int fd);

    [DllImport("libc", SetLastError = true)]
    static extern int tcgetattr(int fd, byte[] termios);

    [DllImport("libc", SetLastError = true)]
    static extern int tcsetattr(int fd, int optionalActions, byte[] termios);

    [DllImport("libc", SetLastError = true)]
    static extern nint read(int fd, byte[] buf, nint count);
}
