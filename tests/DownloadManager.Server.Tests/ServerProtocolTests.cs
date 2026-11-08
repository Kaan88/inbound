using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Persistence;
using TestServer;

namespace DownloadManager.Server.Tests;

public sealed class ServerProtocolTests
{
    [Fact]
    public async Task AWebSocketClientGetsTheHelloThenTheSnapshotThenAnswersInOrder()
    {
        await using var server = await ServerHarness.StartAsync();
        await using var client = await server.ConnectAsync();

        var hello = await client.ReadAsync();
        Assert.Equal(MessageTypes.ServerHello, hello!.Type);
        var helloDto = hello.GetPayload<HostHelloDto>();
        Assert.Equal(Protocol.Version, helloDto.ProtocolVersion);
        Assert.Contains("folders", helloDto.Capabilities);
        Assert.DoesNotContain("tray", helloDto.Capabilities);

        var snapshot = await client.ReadAsync();
        Assert.Equal(MessageTypes.JobsSnapshot, snapshot!.Type);
        var settings = snapshot.GetPayload<SnapshotDto>().Settings;
        // A fresh data folder downloads into the first root.
        Assert.Equal(Path.GetFullPath(server.Root), settings.DefaultDownloadDirectory);

        var pong = await client.AskAsync<HostActionResult>(MessageTypes.HostPing, new { });
        Assert.True(pong.Ok);
        Assert.Equal("pong", pong.Message);
    }

    [Fact]
    public async Task ADownloadAddedOverTheWebSocketCompletesIntoTheRoot()
    {
        await using var files = await TestServerHost.StartAsync();
        await using var server = await ServerHarness.StartAsync();
        await using var client = await server.ConnectAsync();
        await client.ReadUntilAsync(message => message.Type == MessageTypes.JobsSnapshot);

        var added = await client.AskAsync<AddJobResponse>(MessageTypes.JobAdd, new AddJobRequest
        {
            Url = $"{files.BaseAddress}/file/server-test.bin?size=262144&seed=7",
            StartMode = "startNow",
        });
        Assert.True(added.Accepted, added.Error);

        var completed = await client.ReadUntilAsync(message =>
            message.Type == MessageTypes.JobEvent && message.GetPayload<JobEventDto>() is { Job.State: "completed" } done && done.Job.Id == added.Job!.Id,
            TimeSpan.FromSeconds(60));
        var job = completed.GetPayload<JobEventDto>().Job;
        Assert.Equal(Path.GetFullPath(server.Root), Path.GetDirectoryName(job.FinalFilePath));
        Assert.Equal(files.ContentFor("server-test.bin", 262144, 7), await File.ReadAllBytesAsync(job.FinalFilePath));
    }

    [Fact]
    public async Task HealthAnswersWithoutAnythingElse()
    {
        await using var server = await ServerHarness.StartAsync();
        using var response = await server.Http.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("ok", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task TheStaticPageIsServedAndSaysThereIsNoSignInYet()
    {
        await using var server = await ServerHarness.StartAsync();
        var page = await server.Http.GetStringAsync("/");
        Assert.Contains("no sign-in yet", page);
    }

    [Fact]
    public async Task PostApiIpcAnswersOneRequestWithItsReply()
    {
        await using var server = await ServerHarness.StartAsync();
        var status = await server.AskAsync<HostStatusDto>(MessageTypes.HostStatus, new { });
        Assert.False(status.TrayAvailable);
        Assert.False(status.NativeHostRegistered);

        var snapshot = await server.AskAsync<SnapshotDto>(MessageTypes.JobsQuery, new { });
        Assert.Empty(snapshot.Jobs);
    }

    [Fact]
    public async Task PostApiIpcRefusesSomethingThatIsNotAnEnvelope()
    {
        await using var server = await ServerHarness.StartAsync();
        using var response = await server.Http.PostAsync("/api/ipc", new StringContent("not json", System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AnotherProtocolVersionIsAnsweredIncompatible()
    {
        await using var server = await ServerHarness.StartAsync();
        var request = IpcMessage.Create(MessageTypes.HostPing, new { }) with { V = Protocol.Version + 1 };
        using var response = await server.Http.PostAsync("/api/ipc", JsonContent.Create(request, options: IpcJson.Options));
        var reply = await response.Content.ReadFromJsonAsync<IpcMessage>(IpcJson.Options);
        Assert.Equal(MessageTypes.ServerIncompatible, reply!.Type);
    }

    [Fact]
    public async Task WhatTheServerCantDoIsRefusedAndProgramSettingsStayAsTheyWere()
    {
        await using var server = await ServerHarness.StartAsync();
        var shutdown = await server.AskAsync<HostActionResult>(MessageTypes.HostShutdown, new { });
        Assert.False(shutdown.Ok);
        var open = await server.AskAsync<HostActionResult>(MessageTypes.HostOpenUi, new HostActionRequest());
        Assert.False(open.Ok);
        var pick = await server.AskAsync<FolderPickResult>(MessageTypes.HostPickFolder, new FolderPickRequest());
        Assert.False(pick.Ok);
        var update = await server.AskAsync<UpdateCheckResult>(MessageTypes.UpdateCheck, new { });
        Assert.NotNull(update.Error);

        var settings = (await server.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
        settings.PostDownloadCommand = "/bin/sh -c true";
        var refused = await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });
        Assert.False(refused.Ok);
        Assert.Null(refused.Settings!.PostDownloadCommand);
    }

    [Fact]
    public async Task SavedPasswordsAreSealedWithTheKeyFileAndOpenAgainAfterARestart()
    {
        var work = ServerHarness.NewWork();
        const string Password = "server-test-password-not-real";
        await using (var server = await ServerHarness.StartAsync(work: work))
        {
            var settings = (await server.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!;
            settings.SiteLogins.Add(new SiteLoginDto { Site = "files.example.invalid", Username = "someone", Password = Password });
            var saved = await server.AskAsync<SettingsResult>(MessageTypes.SettingsUpdate, new SettingsUpdateRequest { Settings = settings });
            Assert.True(saved.Ok, saved.Error);
        }

        var json = await File.ReadAllTextAsync(Path.Combine(work, "data", "settings.json"));
        Assert.DoesNotContain(Password, json);
        Assert.Contains(AesGcmSecretProtector.BlobPrefix, json);
        Assert.True(File.Exists(Path.Combine(work, "data", AesGcmSecretProtector.DefaultFileName)));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite,
                File.GetUnixFileMode(Path.Combine(work, "data", AesGcmSecretProtector.DefaultFileName)));
        }

        await using (var again = await ServerHarness.StartAsync(work: work))
        {
            var login = Assert.Single((await again.AskAsync<SettingsResult>(MessageTypes.SettingsGet, new { })).Settings!.SiteLogins);
            Assert.True(login.HasPassword);
            Assert.Null(login.Password);
            // The password opens with the same key file: the store would have dropped it otherwise.
            Assert.Equal(Password, SecretProtector.Unprotect(ReadSealedLoginPassword(json)));
        }
    }

    private static string ReadSealedLoginPassword(string settingsJson)
    {
        using var document = JsonDocument.Parse(settingsJson);
        foreach (var login in document.RootElement.EnumerateObject().First(property => property.NameEquals("siteLogins") || property.NameEquals("SiteLogins")).Value.EnumerateArray())
        {
            foreach (var property in login.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && property.Value.GetString()!.StartsWith(AesGcmSecretProtector.BlobPrefix, StringComparison.Ordinal))
                    return property.Value.GetString()!;
            }
        }

        throw new InvalidOperationException("No sealed password in the settings file.");
    }
}
