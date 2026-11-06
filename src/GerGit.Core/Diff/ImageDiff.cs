namespace GerGit.Core.Diff;

/// <summary>Which binary files get the image diff (by extension), and the difference image of Highlight pixels.</summary>
public static class ImageDiff
{
    static readonly string[] Extensions = [".bmp", ".gif", ".png", ".jpg", ".jpeg", ".ico", ".tga", ".webp"];

    /// <summary>A binary diff of this path is shown as an image diff (extension, case-insensitive).</summary>
    public static bool IsImagePath(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Two 8-bit BGRA buffers of the same size in, one out: a pixel differs when any channel differs by 5 or
    /// more; differing pixels become opaque magenta (#FF00FF), all others transparent.</summary>
    public static byte[] Difference(ReadOnlySpan<byte> oldBgra, ReadOnlySpan<byte> newBgra)
    {
        if (oldBgra.Length != newBgra.Length || oldBgra.Length % 4 != 0)
            throw new ArgumentException("Both images must be BGRA buffers of the same size.");
        var result = new byte[oldBgra.Length];
        for (int i = 0; i < result.Length; i += 4)
            for (int c = 0; c < 4; c++)
                if (Math.Abs(oldBgra[i + c] - newBgra[i + c]) >= 5)
                {
                    result[i] = 0xFF;     // B
                    result[i + 2] = 0xFF; // R
                    result[i + 3] = 0xFF; // A
                    break;
                }
        return result;
    }
}
