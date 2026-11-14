using DownloadManager.Host;
using Xunit;

namespace DownloadManager.Integration.Tests;

/// <summary>EXT.B8: which files "open" starts directly on Linux. Nothing is started here; throwaway files only.</summary>
public sealed class LinuxOpenRuleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "inbound-open-rule-" + Guid.NewGuid().ToString("N"));

    public LinuxOpenRuleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (DirectoryNotFoundException) { }
    }

    private string Write(string name, byte[] bytes)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Elf(byte[]? marker, int length = 64)
    {
        var bytes = new byte[length];
        bytes[0] = 0x7F; bytes[1] = 0x45; bytes[2] = 0x4C; bytes[3] = 0x46;
        if (marker is not null) Array.Copy(marker, 0, bytes, 8, Math.Min(marker.Length, length - 8));
        return bytes;
    }

    [Fact]
    public void Type2AppImageIsDetected() =>
        Assert.True(LinuxOpenRule.IsAppImage(Write("a.AppImage", Elf([0x41, 0x49, 0x02]))));

    [Fact]
    public void Type1AppImageIsDetected() =>
        Assert.True(LinuxOpenRule.IsAppImage(Write("a", Elf([0x41, 0x49, 0x01]))));

    [Fact]
    public void ElfWithoutTheAppImageMagicIsNotDetected() =>
        Assert.False(LinuxOpenRule.IsAppImage(Write("tool.AppImage", Elf(null))));

    [Fact]
    public void UnknownAppImageTypeIsNotDetected() =>
        Assert.False(LinuxOpenRule.IsAppImage(Write("a", Elf([0x41, 0x49, 0x03]))));

    [Fact]
    public void TextFileNamedAppImageIsNotDetected() =>
        Assert.False(LinuxOpenRule.IsAppImage(Write("x.AppImage", "#!/bin/sh\necho hello world\n"u8.ToArray())));

    [Fact]
    public void FileShorterThanElevenBytesIsNotDetected() =>
        Assert.False(LinuxOpenRule.IsAppImage(Write("short", Elf([0x41, 0x49], length: 10))));

    [Fact]
    public void MissingFileOrFolderIsNotDetected()
    {
        Assert.False(LinuxOpenRule.IsAppImage(Path.Combine(_root, "nope")));
        Assert.False(LinuxOpenRule.IsAppImage(_root));
    }

    [Fact]
    public void OwnerExecuteIsAddedAndOtherBitsStay()
    {
        if (!OperatingSystem.IsLinux()) return;
        var path = Write("a.AppImage", Elf([0x41, 0x49, 0x02]));
        var before = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(path, before);

        LinuxOpenRule.MakeOwnerExecutable(path);

        Assert.Equal(before | UnixFileMode.UserExecute, File.GetUnixFileMode(path));
    }

    [Fact]
    public void ExecuteBitsOfGroupAndOtherAreNotAdded()
    {
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserExecute, LinuxOpenRule.WithOwnerExecute(UnixFileMode.UserRead));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.GroupExecute,
            LinuxOpenRule.WithOwnerExecute(UnixFileMode.UserRead | UnixFileMode.GroupExecute));
    }
}
