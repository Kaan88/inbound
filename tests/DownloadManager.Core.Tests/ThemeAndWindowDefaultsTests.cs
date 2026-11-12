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
    [InlineData(true, "System", "Dark")]
    [InlineData(false, "Dark", "Light")]
    [InlineData(null, "Dark", "Dark")]
    [InlineData(null, null, "System")]
    public void AKeptDarkModeChoiceIsTheThemeInUse(bool? kept, string? setting, string expected) =>
        Assert.Equal(expected, ThemeChoices.InUse(kept, setting));

    [Theory]
    [InlineData("System", true, true)]
    [InlineData("System", false, false)]
    [InlineData("Light", true, false)]
    [InlineData("Dark", false, true)]
    public void SystemFollowsWindowsAndTheOthersHoldTheirOwn(string theme, bool windowsDark, bool expected) =>
        Assert.Equal(expected, ThemeChoices.IsDark(theme, windowsDark));

    [Fact]
    public void TheMenuToggleKeepsLightOrDarkAndTheMigrationReadsItBack()
    {
        Assert.True(ThemeChoices.ToDarkMode(ThemeChoices.Dark));
        Assert.False(ThemeChoices.ToDarkMode(ThemeChoices.Light));
        Assert.Null(ThemeChoices.ToDarkMode(ThemeChoices.System));
        foreach (var theme in new[] { ThemeChoices.Light, ThemeChoices.Dark })
            Assert.Equal(theme, ThemeChoices.FromDarkMode(ThemeChoices.ToDarkMode(theme)));
    }
}

/// <summary>Source checks for UI.B8 and UI.R23 that a unit test of the WinUI types cannot make outside a window.</summary>
public sealed class WinUiSourceRulesTests
{
    private static readonly string WinUi = FindWinUi();

    private static IEnumerable<(string File, string Text)> Sources() =>
        Directory.EnumerateFiles(WinUi, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Select(path => (Path.GetRelativePath(WinUi, path), File.ReadAllText(path)));

    [Fact]
    public void EveryContentDialogIsGivenItsWindowsXamlRoot()
    {
        // DialogGuard refuses a dialog without one, so one missing here would never show.
        var missing = new List<string>();
        var count = 0;
        foreach (var (file, text) in Sources())
        {
            for (var at = text.IndexOf("new ContentDialog", StringComparison.Ordinal); at >= 0; at = text.IndexOf("new ContentDialog", at + 1, StringComparison.Ordinal))
            {
                count++;
                var open = text.IndexOf('{', at);
                var close = MatchingBrace(text, open);
                if (open < 0 || close < 0 || !text[open..close].Contains("XamlRoot", StringComparison.Ordinal)) missing.Add($"{file}:{LineOf(text, at)}");
            }
        }

        Assert.True(count > 0);
        Assert.Empty(missing);
    }

    [Fact]
    public void EveryWindowFollowsAppTheme()
    {
        var windows = Sources().Where(source => System.Text.RegularExpressions.Regex.IsMatch(source.Text, @"class \w+\s*:\s*Window\b")).ToList();
        Assert.NotEmpty(windows);
        Assert.Empty(windows.Where(source => !source.Text.Contains("AppTheme.Track(", StringComparison.Ordinal)).Select(source => source.File));
    }

    [Fact]
    public void OnlyAppThemeReadsTheOldDarkModeFromDisk()
    {
        Assert.Empty(Sources().Where(source => source.File != "AppTheme.cs" && source.Text.Contains("Load().DarkMode", StringComparison.Ordinal)).Select(source => source.File));
    }

    [Fact]
    public void OptionsThemeBoxStartsFromTheThemeInUse()
    {
        // A Dark Mode click the Host has yet to save is kept in _uiState; starting from the Host's Theme let OK undo it.
        var main = File.ReadAllText(Path.Combine(WinUi, "MainWindow.xaml.cs"));
        Assert.Contains("Array.IndexOf(themeValues, ThemeChoices.InUse(_uiState.DarkMode, settings.Theme))", main, StringComparison.Ordinal);
        Assert.DoesNotContain("Array.IndexOf(themeValues, ThemeChoices.Normalize(settings.Theme))", main, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUpdateIsDeferredOnlyWhenADialogWasShown()
    {
        // DialogGuard returns null when it refuses a dialog (another one is open); that is not "Later".
        var checkedCount = 0;
        var unguarded = new List<string>();
        foreach (var (file, text) in Sources())
        {
            for (var at = text.IndexOf("DeferUpdateAsync(", StringComparison.Ordinal); at >= 0; at = text.IndexOf("DeferUpdateAsync(", at + 1, StringComparison.Ordinal))
            {
                var shown = text.LastIndexOf("DialogGuard.ShowAsync(", at, StringComparison.Ordinal);
                if (shown < 0) continue;
                checkedCount++;
                if (!text[shown..at].Contains("is null", StringComparison.Ordinal)) unguarded.Add($"{file}:{LineOf(text, at)}");
            }
        }

        // UI.R40: Check for Updates is one dialog now, with one place that puts the update off.
        Assert.True(checkedCount >= 1);
        Assert.Empty(unguarded);
    }

    private static int MatchingBrace(string text, int open)
    {
        if (open < 0) return -1;
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string FindWinUi()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory.FullName, "src", "DownloadManager.Desktop.WinUI");
    }
}
