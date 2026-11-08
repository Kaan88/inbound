using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Server.Auth;

namespace DownloadManager.Server.Tests;

/// <summary>Web UI sessions that survive a restart (owner, 2026-11-08: "Survive restarts too"), kept as hashes only.</summary>
public sealed class SessionPersistenceTests
{
    private static string HashOf(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));

    private static void DeleteWork(string work)
    {
        try
        {
            Directory.Delete(work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The next build's clean removes test-work.
        }
    }

    [Fact]
    public async Task ASessionSurvivesARestartAndASignOutStaysSignedOut()
    {
        var work = ServerHarness.NewWork();
        try
        {
            string first, second;
            await using (var server = await AuthServer.StartAsync(work: work))
            {
                using var a = await AuthServer.SetUpAsync(server);
                using var b = new AuthClient(server.Base);
                Assert.Equal(HttpStatusCode.OK, (await b.PostAsync("/api/auth/signin", new { password = AuthServer.Password })).StatusCode);
                first = a.Session!;
                second = b.Session!;
            }

            // Only the hashes are on disk, never a cookie's id.
            var path = Path.Combine(work, "data", SessionStore.FileName);
            var saved = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain(first, saved);
            Assert.DoesNotContain(second, saved);
            Assert.Contains(HashOf(first), saved);
            Assert.Contains(HashOf(second), saved);
            Assert.False(File.Exists(path + ".tmp"));
            if (!OperatingSystem.IsWindows())
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));

            await using (var server = await AuthServer.StartAsync(work: work))
            {
                using var a = new AuthClient(server.Base) { Session = first };
                using var b = new AuthClient(server.Base) { Session = second };
                Assert.Equal(HttpStatusCode.OK, (await a.IpcAsync(MessageTypes.HostPing, new { })).Status);
                Assert.Equal(HttpStatusCode.OK, (await b.IpcAsync(MessageTypes.HostPing, new { })).Status);
                // A restored session opens the WebSocket too.
                await using (var socket = await server.ConnectAsync(sessionCookie: first))
                    Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);

                Assert.Equal(HttpStatusCode.OK, (await b.PostAsync("/api/auth/signout")).StatusCode);
            }

            Assert.DoesNotContain(HashOf(second), await File.ReadAllTextAsync(path));
            await using (var server = await AuthServer.StartAsync(work: work))
            {
                using var a = new AuthClient(server.Base) { Session = first };
                using var b = new AuthClient(server.Base) { Session = second };
                Assert.Equal(HttpStatusCode.OK, (await a.IpcAsync(MessageTypes.HostPing, new { })).Status);
                Assert.Equal(HttpStatusCode.Unauthorized, (await b.IpcAsync(MessageTypes.HostPing, new { })).Status);
            }
        }
        finally
        {
            DeleteWork(work);
        }
    }

    [Fact]
    public async Task APasswordChangeAndTheTimeLimitsStillEndSavedSessions()
    {
        var work = ServerHarness.NewWork();
        var time = new ManualTime(DateTimeOffset.UtcNow);
        try
        {
            string first, second;
            await using (var server = await AuthServer.StartAsync(time, work: work))
            {
                using var a = await AuthServer.SetUpAsync(server);
                using var b = new AuthClient(server.Base);
                Assert.Equal(HttpStatusCode.OK, (await b.PostAsync("/api/auth/signin", new { password = AuthServer.Password })).StatusCode);
                first = a.Session!;
                second = b.Session!;
                Assert.Equal(HttpStatusCode.OK,
                    (await a.PostAsync("/api/auth/password", new { current = AuthServer.Password, password = "the new password" })).StatusCode);
            }

            // The browser that changed it stays signed in across the restart; the other one doesn't come back.
            await using (var server = await AuthServer.StartAsync(time, work: work))
            {
                using var a = new AuthClient(server.Base) { Session = first };
                using var b = new AuthClient(server.Base) { Session = second };
                Assert.Equal(HttpStatusCode.OK, (await a.IpcAsync(MessageTypes.HostPing, new { })).Status);
                Assert.Equal(HttpStatusCode.Unauthorized, (await b.IpcAsync(MessageTypes.HostPing, new { })).Status);
            }

            // 24 hours without a request while the server was down: gone on the next start, and from the file.
            time.Advance(TimeSpan.FromHours(24) + TimeSpan.FromMinutes(1));
            await using (var server = await AuthServer.StartAsync(time, work: work))
            {
                using var a = new AuthClient(server.Base) { Session = first };
                Assert.Equal(HttpStatusCode.Unauthorized, (await a.IpcAsync(MessageTypes.HostPing, new { })).Status);
                using var file = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(work, "data", SessionStore.FileName)));
                Assert.Equal(0, file.RootElement.GetProperty("sessions").GetArrayLength());
            }
        }
        finally
        {
            DeleteWork(work);
        }
    }

    [Fact]
    public void SevenDaysEndASessionWhateverItDoesAndAnotherPasswordDropsTheFile()
    {
        var work = ServerHarness.NewWork();
        try
        {
            var time = new ManualTime(DateTimeOffset.UtcNow);
            var path = Path.Combine(work, SessionStore.FileName);
            var stamp = "stamp-one";
            SessionStore Open() => new(time, TimeSpan.FromHours(24), TimeSpan.FromDays(7), path, () => stamp, null);

            var (id, _) = Open().Create();
            // Used every 23 hours, with a restart before each use, it lives until 7 days after sign-in and no longer.
            for (var restart = 0; restart < 7; restart++)
            {
                time.Advance(TimeSpan.FromHours(23));
                Assert.NotNull(Open().Find(id));
            }

            time.Advance(TimeSpan.FromHours(7));
            Assert.Null(Open().Find(id));

            var (other, _) = Open().Create();
            Assert.NotNull(Open().Find(other));
            stamp = "another password";
            Assert.Null(Open().Find(other));
            Assert.False(File.Exists(path));
        }
        finally
        {
            DeleteWork(work);
        }
    }

    [Fact]
    public void LastSeenIsWrittenAtMostEveryFewMinutesAndOnFlush()
    {
        var work = ServerHarness.NewWork();
        try
        {
            var time = new ManualTime(DateTimeOffset.UtcNow);
            var path = Path.Combine(work, SessionStore.FileName);
            var store = new SessionStore(time, TimeSpan.FromHours(24), TimeSpan.FromDays(7), path, () => "stamp", null);
            Assert.Equal(TimeSpan.FromMinutes(10), store.SaveEvery);
            var (id, _) = store.Create();
            var written = File.ReadAllBytes(path);

            // A browser busy for nine minutes writes nothing.
            for (var i = 0; i < 9; i++)
            {
                time.Advance(TimeSpan.FromMinutes(1));
                Assert.NotNull(store.Find(id));
            }

            Assert.Equal(written, File.ReadAllBytes(path));

            time.Advance(TimeSpan.FromMinutes(1));
            Assert.NotNull(store.Find(id));
            var touched = File.ReadAllBytes(path);
            Assert.NotEqual(written, touched);

            time.Advance(TimeSpan.FromMinutes(1));
            Assert.NotNull(store.Find(id));
            Assert.Equal(touched, File.ReadAllBytes(path));
            store.Flush();
            Assert.NotEqual(touched, File.ReadAllBytes(path));
            var flushed = File.ReadAllBytes(path);
            store.Flush();
            Assert.Equal(flushed, File.ReadAllBytes(path));
        }
        finally
        {
            DeleteWork(work);
        }
    }

    [Fact]
    public void ADamagedFileIsRemovedAndEveryoneSignsInAgain()
    {
        var work = ServerHarness.NewWork();
        try
        {
            var path = Path.Combine(work, SessionStore.FileName);
            File.WriteAllText(path, "{ not json");
            var warnings = new List<string>();
            var store = new SessionStore(TimeProvider.System, TimeSpan.FromHours(24), TimeSpan.FromDays(7), path, () => "stamp", warnings.Add);
            Assert.Equal(0, store.Count);
            Assert.False(File.Exists(path));
            Assert.Single(warnings);

            // A saved entry that isn't a hash is dropped, the rest kept.
            var good = HashOf("a session id");
            var now = DateTimeOffset.UtcNow;
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                version = 1,
                passwordStamp = "stamp",
                sessions = new object[]
                {
                    new { hash = good, createdAt = now, lastSeenAt = now },
                    new { hash = "the raw id", createdAt = now, lastSeenAt = now },
                },
            }));
            store = new SessionStore(TimeProvider.System, TimeSpan.FromHours(24), TimeSpan.FromDays(7), path, () => "stamp", null);
            Assert.Equal(1, store.Count);
            Assert.NotNull(store.Find("a session id"));
            Assert.DoesNotContain("the raw id", File.ReadAllText(path));
        }
        finally
        {
            DeleteWork(work);
        }
    }
}
