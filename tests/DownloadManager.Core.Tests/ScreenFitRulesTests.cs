using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>The Linux (Uno) window opens and comes back inside the screen it is on, even one smaller than where it was saved.</summary>
public sealed class ScreenFitRulesTests
{
    [Fact]
    public void The_first_main_window_fits_a_small_screen_and_keeps_its_size_on_a_big_one()
    {
        // 1640 x 940 is the main window's first size; Xvfb's default screen is 1280 x 1024, a small laptop 1366 x 768.
        Assert.Equal(new PixelSize(1256, 940), WindowSizeRules.FitOnScreen(1640, 940, new ScreenRect(0, 0, 1280, 1024)));
        Assert.Equal(new PixelSize(1342, 696), WindowSizeRules.FitOnScreen(1640, 940, new ScreenRect(0, 0, 1366, 720)));
        Assert.Equal(new PixelSize(1640, 940), WindowSizeRules.FitOnScreen(1640, 940, new ScreenRect(0, 0, 2560, 1400)));
    }

    [Fact]
    public void An_unknown_screen_gets_a_size_that_fits_common_small_screens()
    {
        Assert.Equal(new PixelSize(WindowSizeRules.UnknownScreenWidth, WindowSizeRules.UnknownScreenHeight), WindowSizeRules.FitOnScreen(1640, 940, null));
        Assert.Equal(new PixelSize(900, 600), WindowSizeRules.FitOnScreen(900, 600, null));
        // An empty rectangle is as good as none.
        Assert.Equal(new PixelSize(1100, 700), WindowSizeRules.FitOnScreen(1640, 940, new ScreenRect(0, 0, 0, 0)));
        Assert.True(WindowSizeRules.UnknownScreenWidth + WindowSizeRules.ScreenMargin <= 1280);
        Assert.True(WindowSizeRules.UnknownScreenHeight + 68 <= 768, "a panel and a title bar on a 1366 x 768 screen");
    }

    [Fact]
    public void A_size_saved_on_a_bigger_screen_comes_back_inside_this_one_and_moved_onto_it()
    {
        var laptop = new ScreenRect(0, 32, 1366, 704);
        var (x, y, size) = WindowSizeRules.OnScreen(1200, 600, 2400, 1300, laptop);
        Assert.Equal(new PixelSize(1342, 680), size);
        Assert.True(x >= laptop.X && x + size.Width <= laptop.X + laptop.Width);
        Assert.True(y >= laptop.Y && y + size.Height <= laptop.Y + laptop.Height);
        Assert.Equal((24, 56), (x, y));
    }

    [Fact]
    public void A_placement_that_fits_is_left_alone_and_an_unknown_screen_keeps_the_place()
    {
        var screen = new ScreenRect(0, 0, 1920, 1040);
        Assert.Equal((100, 80, new PixelSize(1200, 800)), WindowSizeRules.OnScreen(100, 80, 1200, 800, screen));
        Assert.Equal((3000, -40, new PixelSize(1100, 700)), WindowSizeRules.OnScreen(3000, -40, 1640, 940, null));
    }

    [Fact]
    public void The_work_area_is_the_monitor_holding_the_point_less_the_panels()
    {
        ScreenRect[] monitors = [new(0, 0, 1920, 1080), new(1920, 0, 1280, 1024)];
        // _NET_WORKAREA: one rectangle over both, less a 32 px top bar.
        var desktop = new ScreenRect(0, 32, 3200, 1048);
        Assert.Equal(new ScreenRect(1920, 32, 1280, 992), WindowSizeRules.WorkAreaFor(monitors, 0, desktop, 2000, 300));
        Assert.Equal(new ScreenRect(0, 32, 1920, 1048), WindowSizeRules.WorkAreaFor(monitors, 0, desktop, 50, 50));
    }

    [Fact]
    public void Without_a_point_on_a_monitor_the_primary_one_is_used_then_the_first()
    {
        ScreenRect[] monitors = [new(0, 0, 1920, 1080), new(1920, 0, 1280, 1024)];
        Assert.Equal(monitors[1], WindowSizeRules.WorkAreaFor(monitors, 1, null, null, null));
        Assert.Equal(monitors[1], WindowSizeRules.WorkAreaFor(monitors, 1, null, 9000, 9000));
        Assert.Equal(monitors[0], WindowSizeRules.WorkAreaFor(monitors, -1, null, null, null));
    }

    [Fact]
    public void Missing_monitors_or_work_area_fall_back_to_what_is_known()
    {
        var desktop = new ScreenRect(0, 27, 1280, 997);
        Assert.Equal(desktop, WindowSizeRules.WorkAreaFor([], -1, desktop, null, null));
        Assert.Null(WindowSizeRules.WorkAreaFor([], -1, null, null, null));
        Assert.Null(WindowSizeRules.WorkAreaFor([new ScreenRect(0, 0, 0, 0)], 0, new ScreenRect(0, 0, 0, 0), null, null));
        // A work area that misses the monitor is ignored.
        Assert.Equal(new ScreenRect(0, 0, 1280, 1024), WindowSizeRules.WorkAreaFor([new ScreenRect(0, 0, 1280, 1024)], 0, new ScreenRect(5000, 0, 100, 100), null, null));
    }

    [Fact]
    public void A_size_in_dips_becomes_screen_pixels_at_the_scale_before_it_is_fitted()
    {
        // 2560 x 1440 screen at scale 2: the main window's 1640 x 940 DIPs would be 3280 x 1880 pixels, so it is cut to
        // the work area less the margin, in pixels, not to a quarter of the screen.
        var work = new ScreenRect(0, 32, 2560, 1408);
        Assert.Equal(new PixelSize(2536, 1384), WindowSizeRules.FitOnScreen(1640, 940, 2, work));
        // A small window is just scaled.
        Assert.Equal(new PixelSize(1040, 600), WindowSizeRules.FitOnScreen(520, 300, 2, work));
        // An unknown or broken scale counts as 1.
        Assert.Equal(new PixelSize(521, 300), WindowSizeRules.FitOnScreen(520.2, 300, double.NaN, work));
        Assert.Equal(new PixelSize(520, 300), WindowSizeRules.FitOnScreen(520, 300, 0.5, work));
        // An unknown screen keeps the window to the conservative default, in pixels.
        Assert.Equal(new PixelSize(WindowSizeRules.UnknownScreenWidth, WindowSizeRules.UnknownScreenHeight),
            WindowSizeRules.FitOnScreen(1640, 940, 1, null));
    }
}
