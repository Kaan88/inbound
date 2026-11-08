using DownloadManager.Core;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

/// <summary>PERF.5 on by default (owner, 2026-11-08), and the Theme setting kept to its three values (UI.R23).</summary>
public sealed class WindowSettingsDefaultsTests
{
    private static StoragePaths NewPaths() =>
        new(Path.Combine(AppContext.BaseDirectory, "test-work", "window-settings-" + Guid.NewGuid().ToString("N"), "state"));

    [Fact]
    public async Task AFreshInstallEndsTheWindowOnClose()
    {
        var loaded = await new JsonSettingsStore(NewPaths()).LoadAsync();
        Assert.True(loaded.CloseWindowCompletely);
    }

    [Fact]
    public async Task SettingsThatNeverStoredTheValueGetItOn()
    {
        var paths = NewPaths();
        paths.EnsureCreated();
        await File.WriteAllTextAsync(paths.SettingsFile, """{ "schemaVersion": 1, "maxConcurrentDownloads": 2 }""");

        var loaded = await new JsonSettingsStore(paths).LoadAsync();
        Assert.True(loaded.CloseWindowCompletely);
        Assert.Equal(2, loaded.MaxConcurrentDownloads);
    }

    [Fact]
    public async Task ASavedOffStaysOff()
    {
        var paths = NewPaths();
        await new JsonSettingsStore(paths).SaveAsync(new AppSettings { CloseWindowCompletely = false });

        var loaded = await new JsonSettingsStore(paths).LoadAsync();
        Assert.False(loaded.CloseWindowCompletely);
    }

    [Fact]
    public async Task AnUpgradedInstallThatStoredOffGetsItOnOnce()
    {
        var paths = NewPaths();
        paths.EnsureCreated();
        // As 0.5.3 saved it: the value stored off, and no marker from a build with the new default.
        await File.WriteAllTextAsync(paths.SettingsFile, """{ "schemaVersion": 1, "closeWindowCompletely": false }""");

        var upgraded = await new JsonSettingsStore(paths).LoadAsync();
        Assert.True(upgraded.CloseWindowCompletely);
        Assert.True(File.Exists(paths.SettingsFile + ".perf5"));

        // The owner's later choice of off is kept from then on.
        upgraded.CloseWindowCompletely = false;
        await new JsonSettingsStore(paths).SaveAsync(upgraded);
        Assert.False((await new JsonSettingsStore(paths).LoadAsync()).CloseWindowCompletely);
    }

    [Theory]
    [InlineData("dark", "Dark")]
    [InlineData("Light", "Light")]
    [InlineData("purple", "System")]
    public async Task TheThemeIsSavedAsOneOfItsThreeValues(string stored, string expected)
    {
        var paths = NewPaths();
        await new JsonSettingsStore(paths).SaveAsync(new AppSettings { Theme = stored });

        var loaded = await new JsonSettingsStore(paths).LoadAsync();
        Assert.Equal(expected, loaded.Theme);
    }
}
