using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

/// <summary>PERF.5's default and UI.R23's Theme setting (owner decisions, 2026-11-08).</summary>
public sealed class ThemeAndWindowDefaultsTests
{
    [Fact]
    public void ClosingTheWindowEndsItByDefault()
    {
        Assert.True(new AppSettings().CloseWindowCompletely);
        Assert.True(new SettingsDto().CloseWindowCompletely);
    }

    [Theory]
    [InlineData(null, "System")]
    [InlineData("", "System")]
    [InlineData("system", "System")]
    [InlineData(" Light ", "Light")]
    [InlineData("DARK", "Dark")]
    [InlineData("blue", "System")]
    public void ThemeIsSystemLightOrDark(string? value, string expected) => Assert.Equal(expected, ThemeChoices.Normalize(value));

    [Fact]
    public void TheOldDarkModeValueMovesToTheMatchingTheme()
    {
        Assert.Equal(ThemeChoices.Dark, ThemeChoices.FromDarkMode(true));
        Assert.Equal(ThemeChoices.Light, ThemeChoices.FromDarkMode(false));
        // Never toggled: the window followed Windows, which System keeps.
        Assert.Null(ThemeChoices.FromDarkMode(null));
    }

    [Theory]
    [InlineData("System", true, true)]
    [InlineData("System", false, false)]
    [InlineData("Light", true, false)]
    [InlineData("Dark", false, true)]
    public void SystemFollowsWindowsAndTheOthersHoldTheirOwn(string theme, bool windowsDark, bool expected) =>
        Assert.Equal(expected, ThemeChoices.IsDark(theme, windowsDark));
}
