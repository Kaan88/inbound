namespace DownloadManager.Tests.Shared;

/// <summary>
/// Many tests were written with Windows paths (<c>C:\files\a.rar</c>) where the drive is beside the point. This gives
/// the same path for the machine running the test: as written on Windows, <c>/files/a.rar</c> elsewhere, so the test
/// checks the same rule on Linux. Tests about drives, UNC paths or NTFS itself stay Windows-only instead.
/// </summary>
internal static class TestPaths
{
    public static string Native(string windowsPath)
    {
        if (OperatingSystem.IsWindows()) return windowsPath;
        var path = windowsPath;
        if (path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':') path = path[2..];
        return path.Replace('\\', '/');
    }
}
