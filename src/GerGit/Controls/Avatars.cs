using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using GerGit.Hosting;
using GerGit.ViewModels;
using SkiaSharp;

namespace GerGit.Controls;

/// <summary>App side of Avatars: decoded pictures by identity and by URL (two LRU caches of 128, downloads
/// shared per key and run off the UI thread by <see cref="AvatarCache{T}"/>) and the generated placeholder.
/// Limitation: no limit on parallel downloads (one request per newly shown author or URL); add a small semaphore in
/// <see cref="Avatars.Download"/>'s caller if long histories with many authors flood the network.</summary>
public static class AvatarImages
{
    static readonly AvatarCache<Bitmap> ByIdentity = new(Decode), ByUrl = new(Decode);

    static readonly IBrush[] Gradients = Avatars.Gradients.Select(g => (IBrush)new ImmutableLinearGradientBrush(
        [new ImmutableGradientStop(0, Color.Parse(g.Top)), new ImmutableGradientStop(1, Color.Parse(g.Bottom))],
        startPoint: new RelativePoint(0, 0, RelativeUnit.Relative), endPoint: new RelativePoint(0, 1, RelativeUnit.Relative))).ToArray();

    // `noreply@github.com`: the white GitHub mark (Assets/BrandIcons.axaml) on GitHub's dark grey.
    static readonly IBrush GitHubLogoBg = new ImmutableSolidColorBrush(Color.Parse("#24292F"));
    static Geometry? s_gitHubMark;
    static Geometry? GitHubMark => s_gitHubMark ??=
        Application.Current?.TryGetResource("Brand.GitHub", null, out var g) == true ? g as Geometry : null;

    /// <summary>The picture for an author e-mail (key = lower-case e-mail). A completed task = known at once; null = placeholder.</summary>
    public static Task<Bitmap?> ForEmail(string email) => ByIdentity.Get(email.Trim().ToLowerInvariant(), Avatars.SourceUrl(email));

    /// <summary>The picture behind an avatar URL of an account or a notification; null = no image (no placeholder).
    /// Only https is loaded, or plain http on a configured GitLab Server's own origin. The check is repeated here for
    /// URLs that older versions saved with the accounts. A refused URL is not cached, so it loads once its GitLab
    /// Server account is added.</summary>
    public static Task<Bitmap?> ForUrl(string? url) =>
        !string.IsNullOrEmpty(url) && AllowedUrl(url) is { } ok ? ByUrl.Get(url, new Uri(ok)) : Task.FromResult<Bitmap?>(null);

    static string? AllowedUrl(string url) => HostingClient.AvatarUrl(url) ?? HostingService.Current.Accounts
        .Where(a => a.Type == HostType.GitLabServer)
        .Select(a => HostingClient.AvatarUrl(url, a.ServerUrl)).FirstOrDefault(u => u is not null);

    /// <summary>The result of a finished load, else null.</summary>
    public static Bitmap? Ready(Task<Bitmap?>? image) => image is { IsCompletedSuccessfully: true } ? image.Result : null;

    /// <summary>Runs <paramref name="redraw"/> on the UI thread once a pending load finishes.</summary>
    public static void WhenLoaded(Task<Bitmap?> image, Action redraw)
    {
        if (!image.IsCompleted) image.ContinueWith(_ => Dispatcher.UIThread.Post(redraw), TaskScheduler.Default);
    }

    /// <summary>Placeholder gradient for an e-mail.</summary>
    public static IBrush PlaceholderBrush(string email) => Gradients[Avatars.GradientIndex(email)];

    /// <summary>White placeholder initials: 22 px at 42 px, scaled to <paramref name="size"/>.</summary>
    public static FormattedText InitialsText(string name, Typeface face, double size) =>
        new(Avatars.Initials(name), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, face, 22 * size / 42, Brushes.White);

    /// <summary>The picture (or placeholder) as a 42 px square with corner radius 4, scaled to <paramref name="rect"/>.</summary>
    public static void Draw(DrawingContext ctx, Rect rect, Bitmap? image, string email, FormattedText? initials)
    {
        var shape = new RoundedRect(rect, 4 * rect.Width / 42);
        if (image is not null)
        {
            using (ctx.PushClip(shape)) ctx.DrawImage(image, rect);
            return;
        }
        if (Avatars.IsGitHubLogo(email))
        {
            ctx.DrawRectangle(GitHubLogoBg, null, shape);
            if (GitHubMark is not { } mark) return;
            // The 24 px mark at 70 % of the square, centred.
            double s = rect.Width * 0.7 / 24, inset = rect.Width * 0.15;
            using (ctx.PushTransform(Matrix.CreateScale(s, s) * Matrix.CreateTranslation(rect.X + inset, rect.Y + inset)))
                ctx.DrawGeometry(Brushes.White, null, mark);
            return;
        }
        ctx.DrawRectangle(PlaceholderBrush(email), null, shape);
        if (initials is not null)
            ctx.DrawText(initials, new Point(rect.X + (rect.Width - initials.Width) / 2, rect.Y + (rect.Height - initials.Height) / 2));
    }

    /// <summary>Largest avatar that is decoded: the size is read from the image header first, so a small file that
    /// declares a huge picture is never expanded in memory.</summary>
    internal const long MaxPixels = 4096L * 4096;

    /// <summary>True when the image header declares more than <see cref="MaxPixels"/>; nothing is decoded.</summary>
    internal static bool TooLarge(byte[] bytes)
    {
        using var codec = SKCodec.Create(new MemoryStream(bytes));
        if (codec?.Info is not { Width: var w, Height: var h } || (long)w * h <= MaxPixels) return false;
        Trace.WriteLine($"Avatar not decoded: {w}×{h} pixels");
        return true;
    }

    // 84 px = the 42 px square at 2x scale; bounds memory (GitHub avatars are up to 460 px) and stays sharp on HiDPI.
    static Bitmap? Decode(byte[] bytes)
    {
        if (TooLarge(bytes)) return null;
        using var stream = new MemoryStream(bytes);
        return Bitmap.DecodeToWidth(stream, 84);
    }
}

/// <summary>An avatar of any size. By identity (<see cref="AuthorName"/> +
/// <see cref="Email"/> set): placeholder until the picture arrives. Otherwise by URL (<see cref="Url"/>, also when
/// null or its binding has no value yet): the picture only, nothing while loading, on failure or without a URL.</summary>
public sealed class AvatarView : Control
{
    public static readonly StyledProperty<string?> AuthorNameProperty = AvaloniaProperty.Register<AvatarView, string?>(nameof(AuthorName));
    public static readonly StyledProperty<string?> EmailProperty = AvaloniaProperty.Register<AvatarView, string?>(nameof(Email));
    public static readonly StyledProperty<string?> UrlProperty = AvaloniaProperty.Register<AvatarView, string?>(nameof(Url));

    Task<Bitmap?>? _image;

    static AvatarView() => AffectsRender<AvatarView>(AuthorNameProperty, EmailProperty, UrlProperty);

    public AvatarView()
    {
        Width = 16;
        Height = 16;
    }

    public string? AuthorName { get => GetValue(AuthorNameProperty); set => SetValue(AuthorNameProperty, value); }
    public string? Email { get => GetValue(EmailProperty); set => SetValue(EmailProperty, value); }
    public string? Url { get => GetValue(UrlProperty); set => SetValue(UrlProperty, value); }

    /// <summary>The downloaded picture once it is known; null while loading, on failure, or for the placeholder.</summary>
    public Bitmap? Image => AvatarImages.Ready(_image);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != EmailProperty && change.Property != UrlProperty) return;
        var image = _image = IsSet(EmailProperty) ? AvatarImages.ForEmail(Email ?? "") : AvatarImages.ForUrl(Url);
        AvatarImages.WhenLoaded(image, () =>
        {
            if (_image == image) InvalidateVisual();
        });
    }

    public override void Render(DrawingContext ctx)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0) return; // squeezed out by its layout
        if (!IsSet(EmailProperty))
        {
            if (Image is { } picture) AvatarImages.Draw(ctx, rect, picture, "", null);
            return;
        }
        AvatarImages.Draw(ctx, rect, Image, Email ?? "",
            AvatarImages.InitialsText(AuthorName ?? "", new Typeface(FontFamily.Default), rect.Width));
    }
}
