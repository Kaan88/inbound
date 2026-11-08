using System.Diagnostics;
using System.Runtime.InteropServices;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Integration.Tests;

[Trait("Platform", "Windows")]
public sealed class ShellFileIconTests
{
    [Fact]
    public void ShellIconsSupportTypesUnknownFilesAndActualExecutableWithoutLaunchingIt()
    {
        if (!OperatingSystem.IsWindows()) return;
        foreach (var name in new[] { "archive.zip", "document.pdf", "music.mp3", "unknown.dm-unknown-type", "no-extension" })
            Validate(ShellFileIconReader.Read(name, null));
        var executable = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        Assert.True(File.Exists(executable));
        var actual = ShellFileIconReader.Read("cmd.exe", executable);
        Validate(actual);
        var generic = ShellFileIconReader.Read("cmd.exe", null);
        Validate(generic);
        Assert.False(actual!.SequenceEqual(generic!), "Existing executable should expose its own Shell icon.");
        var missing = Path.Combine(AppContext.BaseDirectory, Guid.NewGuid() + ".exe");
        Assert.Equal(generic, ShellFileIconReader.Read("cmd.exe", missing));
    }

    [Fact]
    public void RepeatedShellLookupReleasesOwnedGdiAndIconHandles()
    {
        if (!OperatingSystem.IsWindows()) return;
        for (var i = 0; i < 5; i++) Validate(ShellFileIconReader.Read("archive.zip", null));
        using var process = Process.GetCurrentProcess();
        var gdi = GetGuiResources(process.Handle, 0);
        var user = GetGuiResources(process.Handle, 1);
        for (var i = 0; i < 100; i++) Validate(ShellFileIconReader.Read("archive.zip", null));
        Assert.InRange((long)GetGuiResources(process.Handle, 0) - gdi, -8, 8);
        Assert.InRange((long)GetGuiResources(process.Handle, 1) - user, -8, 8);
    }

    private static void Validate(byte[]? pixels)
    {
        Assert.NotNull(pixels);
        Assert.Equal(ShellFileIconReader.Size * ShellFileIconReader.Size * 4, pixels.Length);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), index => pixels[index * 4 + 3] > 0);
        Assert.Contains(Enumerable.Range(0, pixels.Length / 4), index => pixels[index * 4 + 3] == 0);
        for (var i = 0; i < pixels.Length; i += 4)
            for (var channel = 0; channel < 3; channel++) Assert.True(pixels[i + channel] <= pixels[i + 3]);
    }

    [DllImport("user32.dll")] private static extern uint GetGuiResources(nint process, uint flags);
}
