using DownloadManager.Core;

namespace DownloadManager.Engine.Tests;

public sealed class FfmpegDiscoveryTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(
        Path.Combine(AppContext.BaseDirectory, "test-work", "ffmpeg-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void PathIsSearchedForTheProgramNameOfThisSystem()
    {
        // Windows programs end in .exe; a Linux image installs plain "ffmpeg".
        var name = OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg";
        var other = OperatingSystem.IsWindows() ? "ffmpeg" : "ffmpeg.exe";
        var empty = Directory.CreateDirectory(Path.Combine(_folder, "empty")).FullName;
        var wrong = Directory.CreateDirectory(Path.Combine(_folder, "wrong")).FullName;
        var right = Directory.CreateDirectory(Path.Combine(_folder, "right")).FullName;
        File.WriteAllBytes(Path.Combine(wrong, other), []);
        File.WriteAllBytes(Path.Combine(right, name), []);

        var path = string.Join(Path.PathSeparator, empty, wrong, right);
        Assert.Equal(Path.Combine(right, name), FfmpegMediaMuxer.FindOnPath(path));
        Assert.Null(FfmpegMediaMuxer.FindOnPath(string.Join(Path.PathSeparator, empty, wrong)));
        Assert.Null(FfmpegMediaMuxer.FindOnPath(null));
    }
}
