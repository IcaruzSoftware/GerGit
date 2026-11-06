using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using GerGit.Controls;
using GerGit.Core;
using GerGit.Core.Git;
using GerGit.ViewModels;
using GerGit.Views;

namespace GerGit.Tests.Ui;

/// <summary>Binary and LFS views: which view a binary diff gets, cards, radio bar and Highlight pixels.</summary>
public sealed class ImageDiffUiTests : IDisposable
{
    readonly TempRepo _repo = new();
    static readonly byte[] Red4 = TestPng.Solid(4, 4, 0xFF0000FF);

    public void Dispose()
    {
        Settings.Set<bool?>("views.diff.imagePixelHighlight", null);
        _repo.Dispose();
    }

    void Write(string path, byte[] bytes) => File.WriteAllBytes(Path.Combine(_repo.Path, path), bytes);

    async Task<(DiffViewModel Vm, Window Window, DiffView View)> OpenAsync()
    {
        var vm = new DiffViewModel(await GitRepository.OpenAsync(_repo.Path));
        var view = new DiffView { DataContext = vm };
        var window = new Window { Width = 900, Height = 600, Content = view };
        window.Show();
        return (vm, window, view);
    }

    static async Task ShowAsync(DiffViewModel vm, Window w, DiffRequest q)
    {
        vm.Show(q);
        await vm.Loading;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    static T Find<T>(DiffView v, string name) where T : Control => v.FindControl<T>(name)!;

    [AvaloniaFact]
    public async Task View_is_chosen_by_extension()
    {
        Write("a.png", Red4);
        Write("bad.png", [0, 1, 2, 3, 0, 0, 0, 0]);
        Write("b.bin", [.. new byte[1500]]);
        _repo.Write("c.txt", "text\n");
        var sha = _repo.Commit("add", 0);
        var (vm, w, view) = await OpenAsync();

        await ShowAsync(vm, w, new DiffRequest(null, sha, "a.png", null));
        Assert.Equal(DiffState.Image, vm.State);
        var card = Assert.Single(vm.Cards);
        Assert.Equal(("created", true, true), (card.Title, card.IsNew, card.HasImage));
        Assert.Equal($"W: 4px | H: 4px ({Red4.Length} bytes)", card.Caption);
        Assert.True(Find<ToggleButton>(view, "HighlightButton").IsEffectivelyVisible);
        Assert.False(Find<StackPanel>(view, "ModeBar").IsVisible); // one side only

        // An image that does not decode: card without preview.
        await ShowAsync(vm, w, new DiffRequest(null, sha, "bad.png", null));
        Assert.Equal(DiffState.Image, vm.State);
        Assert.False(Assert.Single(vm.Cards).HasImage);

        // Other binary: unknown-binary card with extension and size.
        await ShowAsync(vm, w, new DiffRequest(null, sha, "b.bin", null));
        Assert.Equal(DiffState.Binary, vm.State);
        card = Assert.Single(vm.Cards);
        Assert.Equal((".bin", "1.46 KB", "1,500 B", false), (card.Extension, card.Caption, card.SizeTip, card.CanSave));
        Assert.False(Find<ToggleButton>(view, "HighlightButton").IsEffectivelyVisible);
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains(".bin", texts);
        Assert.Contains("1.46 KB", texts);

        await ShowAsync(vm, w, new DiffRequest(null, sha, "c.txt", null));
        Assert.Equal(DiffState.Text, vm.State);
        Assert.False(Find<ItemsControl>(view, "CardList").IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public async Task Radio_bar_and_highlight_pixels_in_local_changes()
    {
        Write("a.png", Red4);
        _repo.Commit("one", 0);
        // Staged: same size, one pixel changed. Unstaged: other dimensions.
        Write("a.png", TestPng.Create(4, 4, (x, y) => x == 1 && y == 2 ? 0x0000FFFFu : 0xFF0000FFu));
        _repo.Git("add", "a.png");
        Write("a.png", TestPng.Solid(2, 3, 0xFF0000FF));
        var (vm, w, view) = await OpenAsync();

        await ShowAsync(vm, w, new DiffRequest(null, "", "a.png", null, Working: WorkingDiff.Staged));
        Assert.Equal(["old", "new"], vm.Cards.Select(c => c.Title));
        Assert.True(Find<StackPanel>(view, "ModeBar").IsEffectivelyVisible);
        var highlight = Find<ToggleButton>(view, "HighlightButton");
        Assert.True(highlight.IsEffectivelyEnabled);
        Assert.False(highlight.IsChecked); // default off
        Assert.Null(vm.Cards[1].Overlay);

        highlight.IsChecked = true;
        Assert.True(Settings.Get("views.diff.imagePixelHighlight", false));
        await vm.Highlighting; // made off the UI thread
        var overlay = vm.Cards[1].Overlay;
        Assert.NotNull(overlay);
        var px = BinaryCard.Bgra(overlay);
        for (int i = 0; i < 16; i++)
            Assert.Equal(i == 2 * 4 + 1 ? [0xFF, 0, 0xFF, 0xFF] : new byte[4], px[(i * 4)..(i * 4 + 4)]);

        // Swipe replaces the cards with the stacked view; the overlay goes with it.
        vm.IsSwipe = true;
        Dispatcher.UIThread.RunJobs();
        w.UpdateLayout();
        Assert.False(Find<ItemsControl>(view, "CardList").IsEffectivelyVisible);
        var stack = Find<ImageStack>(view, "Stack");
        Assert.True(stack.IsEffectivelyVisible);
        Assert.Same(overlay, stack.Overlay);
        Assert.NotNull(stack.DividerX);

        // Different dimensions: radio bar still there, Highlight pixels disabled and unchecked; mode back to Side-by-Side.
        await ShowAsync(vm, w, new DiffRequest(null, "", "a.png", null, Working: WorkingDiff.Unstaged));
        Assert.Equal(ImageMode.SideBySide, vm.Mode);
        Assert.True(Find<StackPanel>(view, "ModeBar").IsEffectivelyVisible);
        Assert.False(highlight.IsEffectivelyEnabled);
        Assert.False(highlight.IsChecked);
        Assert.Equal("W: 2px | H: 3px (" + new FileInfo(Path.Combine(_repo.Path, "a.png")).Length + " bytes)", vm.Cards[1].Caption);

        // One side does not decode: no radio bar.
        Write("a.png", [0, 1, 2, 3, 0, 0, 0, 0]);
        await ShowAsync(vm, w, new DiffRequest(null, "", "a.png", null, Working: WorkingDiff.Unstaged));
        Assert.Equal(DiffState.Image, vm.State);
        Assert.False(Find<StackPanel>(view, "ModeBar").IsVisible);
    }

    [AvaloniaFact]
    public async Task Renamed_image_old_card_uses_old_path_and_blob()
    {
        var rnd = new Random(1);
        var before = TestPng.Create(64, 64, (_, _) => (uint)rnd.Next() | 0xFF);
        byte[] after = [.. before, 0, 1, 2, 3]; // bytes after IEND: similar enough for git's rename detection
        Write("a.png", before);
        _repo.Commit("one", 0);
        File.Delete(Path.Combine(_repo.Path, "a.png"));
        Write("b.png", after);
        var sha = _repo.Commit("rename", 1);
        var (vm, w, _) = await OpenAsync();

        await ShowAsync(vm, w, new DiffRequest(null, sha, "b.png", "a.png"));
        Assert.Equal(DiffState.Image, vm.State);
        Assert.Equal([("old", "a.png"), ("new", "b.png")], vm.Cards.Select(c => (c.Title, c.FileName)));
        Assert.Equal(before, vm.Cards[0].Bytes);
        Assert.Equal(after, vm.Cards[1].Bytes);
    }

    [AvaloniaFact]
    public async Task Untracked_image_above_5_MiB_is_not_too_large()
    {
        var big = new byte[6 * 1024 * 1024];
        Write("big.png", big);
        Write("big.bin", big);
        var (vm, w, _) = await OpenAsync();

        await ShowAsync(vm, w, new DiffRequest(null, "", "big.png", null, Working: WorkingDiff.Untracked));
        Assert.Equal(DiffState.Image, vm.State);
        Assert.Equal("6.00 MB", Assert.Single(vm.Cards).Caption); // does not decode: card without preview

        // Above 64 MiB the bytes are not read at all (sparse file: no 65 MiB written to disk).
        using (var huge = File.Create(Path.Combine(_repo.Path, "huge.png"))) huge.SetLength(65L * 1024 * 1024);
        await ShowAsync(vm, w, new DiffRequest(null, "", "huge.png", null, Working: WorkingDiff.Untracked));
        Assert.Equal(DiffState.Image, vm.State);
        var card = Assert.Single(vm.Cards);
        Assert.Equal(("65.0 MB", false, false), (card.Caption, card.CanSave, card.HasImage));
        await ShowAsync(vm, w, new DiffRequest(null, "", "big.bin", null, Working: WorkingDiff.Untracked));
        Assert.Equal(DiffState.TooLarge, vm.State);
        Assert.Empty(vm.Cards);
    }

    [AvaloniaFact]
    public async Task Image_with_huge_dimensions_is_not_decoded_and_says_why()
    {
        var bomb = TestPng.HeaderOnly(30000, 30000); // a few hundred bytes that would decode to 3.6 GB
        Assert.True(bomb.Length < 1000);
        Write("bomb.png", bomb);
        var (vm, w, view) = await OpenAsync();

        await ShowAsync(vm, w, new DiffRequest(null, "", "bomb.png", null, Working: WorkingDiff.Untracked));
        Assert.Equal(DiffState.Image, vm.State);
        var card = Assert.Single(vm.Cards);
        Assert.Equal((false, "Image too large to preview (30000×30000)"), (card.HasImage, card.Note));
        var texts = view.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Image too large to preview (30000×30000)", texts);

        // Below the limit the header check lets the image through.
        using var small = BinaryCard.Decode(Red4, "a.png", out var note);
        Assert.Equal((4, null), (small?.PixelSize.Width, note));
    }

    [AvaloniaFact]
    public async Task Difference_compares_straight_alpha()
    {
        // Half-transparent pixels whose blue differs by 6: premultiplied (x 128/255) the difference would be 3.
        Write("a.png", TestPng.Create(2, 1, (x, _) => x == 0 ? 0xC8643280u : 0x10203040u));
        _repo.Commit("one", 0);
        Write("a.png", TestPng.Create(2, 1, (x, _) => x == 0 ? 0xC8643880u : 0x10203040u));
        var (vm, w, _) = await OpenAsync();
        await ShowAsync(vm, w, new DiffRequest(null, "", "a.png", null, Working: WorkingDiff.Unstaged));

        var bgra = BinaryCard.Bgra(vm.OldImage!);
        Assert.InRange(bgra[0], 0x31, 0x33); // straight B of 0x32, not the premultiplied 0x19
        Assert.Equal(0x80, bgra[3]);
        Assert.Null(vm.Overlay); // made only while Highlight pixels is on
        vm.HighlightPixels = true;
        await vm.Highlighting;
        Assert.Equal([0xFF, 0, 0xFF, 0xFF, 0, 0, 0, 0], BinaryCard.Bgra(vm.Overlay!));

        // With the setting on, the next load makes the difference together with the cards.
        await ShowAsync(vm, w, new DiffRequest(null, "", "a.png", null, Working: WorkingDiff.Unstaged));
        Assert.Equal([0xFF, 0, 0xFF, 0xFF, 0, 0, 0, 0], BinaryCard.Bgra(vm.Overlay!));
    }

    [AvaloniaFact]
    public async Task Refresh_of_the_same_file_keeps_mode_opacity_and_divider()
    {
        Write("a.png", Red4);
        Write("b.png", TestPng.Solid(4, 4, 0x0000FFFF));
        _repo.Commit("one", 0);
        Write("a.png", TestPng.Solid(4, 4, 0x00FF00FF));
        Write("b.png", Red4);
        var (vm, w, view) = await OpenAsync();
        var q = new DiffRequest(null, "", "a.png", null, Working: WorkingDiff.Unstaged);
        await ShowAsync(vm, w, q);
        vm.IsOnionSkin = true;
        vm.OnionOpacity = 0.4;
        vm.SwipePosition = 0.25;
        var first = vm.Cards[0].Image;

        await ShowAsync(vm, w, q); // Local Changes re-shows an equal request on every status refresh
        Assert.Equal((ImageMode.OnionSkin, 0.4, 0.25), (vm.Mode, vm.OnionOpacity, vm.SwipePosition));
        Assert.NotSame(first, vm.Cards[0].Image);
        Assert.Equal(0.25, Find<ImageStack>(view, "Stack").Split);
        using (w.CaptureRenderedFrame()) { } // the previous bitmaps are disposed: rendering must still work

        await ShowAsync(vm, w, q with { Path = "b.png" }); // another file: back to the defaults
        Assert.Equal((ImageMode.SideBySide, 1.0, 0.5), (vm.Mode, vm.OnionOpacity, vm.SwipePosition));
    }
}
