using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using GerGit.Core;
using GerGit.Core.Diff;
using SkiaSharp;

namespace GerGit.ViewModels;

/// <summary>One card of a binary diff (image or other binary file): title, size line and either the image preview or the
/// unknown-binary card (file icon + extension). <see cref="Image"/> is null for unknown binaries and for images that did
/// not decode (card without preview). <see cref="Note"/> says why an image has no preview (too large).
/// Limitation: no LFS card or LFS badges.</summary>
public sealed partial class BinaryCard(string title, bool isNew, string path, BinarySide side, Bitmap? image, string? note = null) : ObservableObject
{
    public string Title => title;
    public bool IsNew => isNew;
    public string FileName => Path.GetFileName(path);
    public string Extension => Path.GetExtension(path);
    public long Size => side.Size;
    public Bitmap? Image => image;
    public bool HasImage => image is not null;
    public string? Note => note;

    /// <summary>Raw bytes for `Save As...` (image diffs only).</summary>
    public byte[]? Bytes => side.Bytes;
    public bool CanSave => side.Bytes is not null;

    /// <summary>The Highlight-pixels layer over this (new) image; null = not shown.</summary>
    [ObservableProperty] public partial Bitmap? Overlay { get; set; }

    /// <summary>Image: `W: &lt;w&gt;px | H: &lt;h&gt;px (&lt;size&gt;)`; otherwise the short size.</summary>
    public string Caption => image is { PixelSize: var p } ? $"W: {p.Width}px | H: {p.Height}px ({ShortSize(Size)})" : ShortSize(Size);

    /// <summary>Exact size, e.g. `12,345 B`.</summary>
    public string SizeTip => Size.ToString("N0", CultureInfo.InvariantCulture) + " B";

    /// <summary>short size: `512 bytes`, `1.23 KB`, `4.56 MB`; 3 significant digits, 1024-based units.</summary>
    public static string ShortSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} bytes";
        string[] units = ["KB", "MB", "GB", "TB"];
        double v = bytes / 1024.0;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v.ToString(v < 10 ? "0.00" : v < 100 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + units[u];
    }

    /// <summary>Images with more pixels are not decoded: a small file can declare a huge picture, and the decoded
    /// bitmap (4 bytes per pixel, copied again for Highlight pixels) would take gigabytes.</summary>
    internal const long MaxPixels = 25_000_000;

    /// <summary>Platform decoder; a failure is logged and gives null. The size is read from the header first: above
    /// <see cref="MaxPixels"/> nothing is decoded and <paramref name="note"/> says why.
    /// Limitation: `.tga` is not decoded first (Skia has no TGA codec), so TGA images show the card without preview.</summary>
    public static Bitmap? Decode(byte[]? bytes, string path, out string? note)
    {
        note = null;
        if (bytes is null) return null;
        try
        {
            using (var codec = SKCodec.Create(new MemoryStream(bytes)))
                if (codec?.Info is { Width: var w, Height: var h } && (long)w * h > MaxPixels)
                {
                    note = $"Image too large to preview ({w}×{h})";
                    Trace.WriteLine($"Not decoded: image '{path}' is {w}×{h} pixels");
                    return null;
                }
            using var s = new MemoryStream(bytes);
            return new Bitmap(s);
        }
        catch (Exception e) // the decoder throws various types for bad data
        {
            Trace.WriteLine($"Cannot decode image '{path}': {e.Message}");
            return null;
        }
    }

    /// <summary>Difference image of two bitmaps with the same pixel size; a failure is logged and gives null.</summary>
    public static Bitmap? Difference(Bitmap oldImage, Bitmap newImage)
    {
        try
        {
            var pixels = ImageDiff.Difference(Bgra(oldImage), Bgra(newImage));
            var size = oldImage.PixelSize;
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try { return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(), size, new Vector(96, 96), size.Width * 4); }
            finally { handle.Free(); }
        }
        catch (Exception e) // pixel formats, sizes and the platform throw various types
        {
            Trace.WriteLine($"Cannot build the difference image: {e.Message}");
            return null;
        }
    }

    /// <summary>The bitmap as tightly packed 8-bit BGRA (straight alpha).</summary>
    internal static byte[] Bgra(Bitmap b)
    {
        var size = b.PixelSize;
        var result = new byte[size.Width * size.Height * 4];
        using var wb = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        b.CopyPixels(fb);
        for (int y = 0; y < size.Height; y++)
            Marshal.Copy(fb.Address + y * fb.RowBytes, result, y * size.Width * 4, size.Width * 4);
        return result;
    }
}
