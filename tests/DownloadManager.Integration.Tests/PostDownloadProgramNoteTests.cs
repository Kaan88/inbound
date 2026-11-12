using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Runtime;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// Review 2:L5: a post-download program saved before the .exe rule is kept by a save of other settings but refused when
/// it would run. The Host says so with the settings, so Options can show it beside the box.
/// </summary>
public sealed class PostDownloadProgramNoteTests
{
    [Fact]
    public void A_saved_program_the_host_would_refuse_comes_with_a_note()
    {
        var refused = OperatingSystem.IsWindows() ? @"C:\tools\after.bat" : "after.sh";
        var problem = DownloadManagerService.MapSettings(new AppSettings { PostDownloadCommand = refused }).PostDownloadCommandProblem;
        Assert.NotNull(problem);
        Assert.Contains(PostDownloadCommandLine.ProgramRefused, problem);

        var allowed = OperatingSystem.IsWindows() ? @"C:\tools\after.exe" : "/usr/local/bin/after";
        Assert.Null(DownloadManagerService.MapSettings(new AppSettings { PostDownloadCommand = allowed }).PostDownloadCommandProblem);
        Assert.Null(DownloadManagerService.MapSettings(new AppSettings()).PostDownloadCommandProblem);
    }

    [Fact]
    public void A_web_session_sees_neither_the_program_nor_its_note()
    {
        var settings = DownloadManagerService.MapSettings(new AppSettings { PostDownloadCommand = "after.bat" });
        var shown = WebSettingsRules.ForWeb(settings);
        Assert.Null(shown.PostDownloadCommand);
        Assert.Null(shown.PostDownloadCommandProblem);
    }
}
