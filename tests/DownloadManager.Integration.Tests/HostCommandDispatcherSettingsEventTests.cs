using DownloadManager.Contracts;
using DownloadManager.Runtime.Ipc;

namespace DownloadManager.Integration.Tests;

/// <summary>Audit 4 H1 (the Host's side): <c>settings.changed</c>, and what a web session may do to a schedule with actions.</summary>
public sealed partial class HostCommandDispatcherTests
{
    [Fact]
    public async Task ASettingsChangeIsAnnouncedInFullToAPipeSessionAndAsShownToAWebSession()
    {
        var web = await WebAsync();
        web.Dispatcher.Start();
        _cleanup.Add(new Disposer(web.Dispatcher.Stop));
        var settings = web.Service.GetSettings();
        settings.MaxConcurrentDownloads = 5;
        // A full path to an .exe: the Host refuses anything else (audit 3).
        var program = Path.Combine(Path.GetTempPath(), "tools", "notepad.exe");
        settings.PostDownloadCommand = program;
        var saved = (await AskAsync(web.Dispatcher, web.Pipe, MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings }))
            .GetPayload<SettingsResult>();
        Assert.True(saved.Ok, saved.Error);

        static bool Announced(IpcMessage frame) =>
            frame.Type == MessageTypes.SettingsChanged && frame.GetPayload<SettingsDto>().MaxConcurrentDownloads == 5;
        await web.Pipe.UntilAsync(frames => frames.Any(Announced));
        await web.Session.UntilAsync(frames => frames.Any(Announced));

        var toPipe = web.Pipe.Frames.Last(Announced).GetPayload<SettingsDto>();
        Assert.Equal(program, toPipe.PostDownloadCommand);
        Assert.True(toPipe.Revision > 0);
        var toWeb = web.Session.Frames.Last(Announced).GetPayload<SettingsDto>();
        Assert.Null(toWeb.PostDownloadCommand);
        Assert.Equal(toPipe.Revision, toWeb.Revision);
    }

    [Fact]
    public async Task AWebSessionCanRemoveAScheduleWithActionsButNotChangeIt()
    {
        var web = await WebAsync();
        var id = Guid.NewGuid();
        var withExit = web.Service.GetSettings();
        withExit.Schedules = [new ScheduleDto { Id = id, Name = "Night", QueueId = 0, Enabled = false, StartTime = "01:00", ExitWhenDone = true }];
        Assert.True((await web.Service.UpdateSettingsAsync(withExit)).Ok);

        async Task<SettingsResult> Save(Action<ScheduleDto> change)
        {
            var settings = web.Service.GetSettings();
            change(settings.Schedules.Single(s => s.Id == id));
            return (await AskAsync(web.Dispatcher, web.Session, MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings }))
                .GetPayload<SettingsResult>();
        }

        foreach (var change in new Action<ScheduleDto>[] { s => s.Enabled = true, s => s.StartTime = "02:00", s => s.StopTime = "05:00", s => s.Days = "Mon" })
        {
            var refused = await Save(change);
            Assert.False(refused.Ok);
            Assert.Equal(WebSettingsRules.ScheduleActionsRefused, refused.Error);
            var kept = web.Service.GetSettings().Schedules.Single(s => s.Id == id);
            Assert.False(kept.Enabled);
            Assert.Equal("01:00", kept.StartTime);
        }

        // Its name is not what it does, so that may change; so may removing it.
        Assert.True((await Save(s => s.Name = "Renamed")).Ok);
        var removed = web.Service.GetSettings();
        removed.Schedules.Clear();
        var result = (await AskAsync(web.Dispatcher, web.Session, MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = removed }))
            .GetPayload<SettingsResult>();
        Assert.True(result.Ok, result.Error);
        Assert.Empty(web.Service.GetSettings().Schedules);
    }
}
