using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// A settings backup through the real CLI and Host: a restore replaces the queues and schedules with the file's, so
/// one deleted after the export comes back (the settings revision would otherwise merge them).
/// </summary>
[Trait("Platform", "Windows")]
public sealed class CliSettingsRestoreTests
{
    [Fact]
    public async Task ASettingsImportBringsBackAScheduleDeletedAfterTheExport()
    {
        await using var host = await SandboxHost.StartAsync("https://example.invalid/v1/api/");
        await using var connection = await host.ConnectAsync();

        var settings = (await connection.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        var scheduleId = Guid.NewGuid();
        settings.Schedules.Add(new ScheduleDto { Id = scheduleId, Name = "Kept", QueueId = 0, Enabled = false, StartTime = "03:00" });
        var added = await connection.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });
        Assert.True(added.Ok, added.Error);
        var exportRevision = added.Settings!.Revision;
        Assert.NotEqual(0L, exportRevision);

        var file = Path.Combine(host.Root, "settings-backup.json");
        var exported = await host.RunCliAsync(null, "settings", "export", file);
        Assert.Equal(0, exported.ExitCode);
        Assert.Equal(0, JsonSerializer.Deserialize<SettingsDto>(await File.ReadAllTextAsync(file), IpcJson.Options)!.Revision);

        var removed = added.Settings!;
        removed.Schedules.RemoveAll(schedule => schedule.Id == scheduleId);
        var deleted = await connection.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = removed });
        Assert.True(deleted.Ok, deleted.Error);
        Assert.DoesNotContain(deleted.Settings!.Schedules, schedule => schedule.Id == scheduleId);

        // Even a file that carries the revision of its export (as this build's first export did) is restored, not
        // merged: merged, the schedule removed after that revision would stay removed.
        var withRevision = JsonSerializer.Deserialize<SettingsDto>(await File.ReadAllTextAsync(file), IpcJson.Options)!;
        withRevision.Revision = exportRevision;
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(withRevision, IpcJson.Options));
        var imported = await host.RunCliAsync(null, "settings", "import", file);
        Assert.Equal(0, imported.ExitCode);

        var restored = (await connection.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        Assert.Contains(restored.Schedules, schedule => schedule.Id == scheduleId);
    }
}
