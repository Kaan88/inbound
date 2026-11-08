using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Server.Auth;

namespace DownloadManager.Server.Tests;

/// <summary>
/// The saved sessions where things go wrong: a write the folder refuses, a password change racing a sign-in or a
/// save, a socket that only listens, and sign-in turned off and on again (review of D2a, 2026-11-08).
/// </summary>
public sealed class SessionDurabilityTests
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
    public void AFailedWriteRemovesTheFileOrEmptiesItSoASignOutStays()
    {
        var work = ServerHarness.NewWork();
        try
        {
            var path = Path.Combine(work, SessionStore.FileName);
            var warnings = new List<string>();
            SessionStore Open() => new(TimeProvider.System, TimeSpan.FromHours(24), TimeSpan.FromDays(7), path, () => "stamp", warnings.Add);

            // The temporary file can't be made (a folder holds its name): the write fails, the old file is deleted.
            var store = Open();
            var (kept, _) = store.Create();
            var (gone, goneSession) = store.Create();
            Directory.CreateDirectory(path + ".tmp");
            store.Remove(goneSession);
            Assert.False(File.Exists(path));
            Assert.Contains(warnings, warning => warning.Contains("could not be saved", StringComparison.Ordinal));
            Assert.Equal(0, Open().Count);
            Directory.Delete(path + ".tmp");

            if (!OperatingSystem.IsWindows()) return; // Below, Windows' sharing rules make the delete fail; Linux allows it.

            // The write fails and the file can't be deleted (another handle shares no delete): it is emptied instead.
            store = Open();
            (kept, _) = store.Create();
            (gone, goneSession) = store.Create();
            Directory.CreateDirectory(path + ".tmp");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                store.Remove(goneSession);
            Assert.Equal(0, new FileInfo(path).Length);
            Assert.Contains(warnings, warning => warning.Contains("emptied", StringComparison.Ordinal));
            warnings.Clear();
            var restarted = Open();
            Assert.Null(restarted.Find(gone));
            Assert.Null(restarted.Find(kept));
            Directory.Delete(path + ".tmp");

            // A folder that refuses the write, the delete and the emptying leaves the old file: that is said loudly.
            store = Open();
            (_, goneSession) = store.Create();
            Directory.CreateDirectory(path + ".tmp");
            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                store.Remove(goneSession);
            Assert.Contains(warnings, warning => warning.Contains("delete it by hand", StringComparison.Ordinal));
        }
        finally
        {
            DeleteWork(work);
        }
    }

    [Fact]
    public void APasswordChangeAndASignInOrSaveRacingItCantLeaveAnOldSessionUnderTheNewStamp()
    {
        var work = ServerHarness.NewWork();
        try
        {
            var time = new ManualTime(DateTimeOffset.UtcNow);
            var path = Path.Combine(work, SessionStore.FileName);
            var stamp = "old";
            SessionStore Open() => new(time, TimeSpan.FromHours(24), TimeSpan.FromDays(7), path, () => stamp, null);
            var store = Open();
            var (mine, mySession) = store.Create();
            var (theirs, theirSession) = store.Create();

            // A failed password save changes nothing.
            Assert.False(store.ChangePassword(() => false, mySession));
            Assert.Equal(2, store.Count);

            Task<(string Id, Session Session)?>? signIn = null;
            Task? touch = null;
            Assert.True(store.ChangePassword(() =>
            {
                // A sign-in that checked the old password, and a request whose last-seen time is due to be written, both
                // arrive while the new password is being saved. Neither can get in until the change is done.
                signIn = Task.Run(() => store.TryCreate(() => stamp == "old"));
                time.Advance(TimeSpan.FromMinutes(11));
                touch = Task.Run(() => store.Touch(theirSession));
                Assert.False(signIn.Wait(300));
                Assert.False(touch.Wait(100));
                stamp = "new";
                return true;
            }, mySession));

            Assert.Null(signIn!.GetAwaiter().GetResult());
            touch!.GetAwaiter().GetResult();
            Assert.Equal(1, store.Count);
            var saved = File.ReadAllText(path);
            Assert.DoesNotContain(HashOf(theirs), saved);
            Assert.Contains(HashOf(mine), saved);

            var restarted = Open();
            Assert.NotNull(restarted.Find(mine));
            Assert.Null(restarted.Find(theirs));
        }
        finally
        {
            DeleteWork(work);
        }
    }

    [Fact]
    public async Task AnOpenSocketKeepsItsSavedLastSeenTimeFresh()
    {
        // 3 s idle: the file's beat is a tenth of it, 300 ms.
        await using var server = await AuthServer.StartAsync(change: options => options with { SessionIdleTimeout = TimeSpan.FromSeconds(3) });
        using var user = await AuthServer.SetUpAsync(server);
        var path = Path.Combine(server.Data, SessionStore.FileName);

        async Task<DateTimeOffset> SavedLastSeenAsync()
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    using var file = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                    return file.RootElement.GetProperty("sessions").EnumerateArray()
                        .Single(session => session.GetProperty("hash").GetString() == HashOf(user.Session!))
                        .GetProperty("lastSeenAt").GetDateTimeOffset();
                }
                catch (IOException) when (attempt < 20)
                {
                    await Task.Delay(25); // caught between the write and the replace
                }
            }
        }

        await using var socket = await server.ConnectAsync(sessionCookie: user.Session);
        Assert.Equal(MessageTypes.ServerHello, (await socket.ReadAsync())!.Type);
        var before = await SavedLastSeenAsync();
        // The page sends nothing; it only listens.
        await Task.Delay(TimeSpan.FromSeconds(1.5));
        var after = await SavedLastSeenAsync();
        Assert.True(after - before >= TimeSpan.FromMilliseconds(600), $"{before:O} -> {after:O}");
        // Long past the idle time, the listening browser is still signed in.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        Assert.Equal(HttpStatusCode.OK, (await user.IpcAsync(MessageTypes.HostPing, new { })).Status);
    }

    [Fact]
    public async Task SignInTurnedOffRemovesTheSavedSessionsSoTurningItOnRestoresNobody()
    {
        var work = ServerHarness.NewWork();
        try
        {
            string session;
            await using (var server = await AuthServer.StartAsync(work: work))
            {
                using var user = await AuthServer.SetUpAsync(server);
                session = user.Session!;
            }

            var path = Path.Combine(work, "data", SessionStore.FileName);
            Assert.True(File.Exists(path));
            await using (var server = await AuthServer.StartAsync(work: work,
                             change: options => options with { SignInRequired = false, AllowUnauthenticated = true }))
            {
                using var anyone = new AuthClient(server.Base);
                Assert.Equal(HttpStatusCode.OK, (await anyone.IpcAsync(MessageTypes.HostPing, new { })).Status);
            }

            Assert.False(File.Exists(path));
            await using (var server = await AuthServer.StartAsync(work: work))
            {
                using var old = new AuthClient(server.Base) { Session = session };
                Assert.Equal(HttpStatusCode.Unauthorized, (await old.IpcAsync(MessageTypes.HostPing, new { })).Status);
                Assert.Equal(HttpStatusCode.OK, (await old.PostAsync("/api/auth/signin", new { password = AuthServer.Password })).StatusCode);
            }
        }
        finally
        {
            DeleteWork(work);
        }
    }

    [Fact]
    public async Task AnUnwritableAuthFileAnswers500ToUnpairAndStillServesTheToken()
    {
        await using var server = await AuthServer.StartAsync();
        using var user = await AuthServer.SetUpAsync(server);
        var token = await PairingTests.PairAsync(server, await PairingTests.NewCodeAsync(user));
        var authFile = Path.Combine(server.Data, AuthStore.FileName);
        Assert.True(File.Exists(authFile));

        // A folder where the temporary file goes: every write of auth.json fails (UnauthorizedAccessException).
        Directory.CreateDirectory(authFile + ".tmp");
        using var extension = new AuthClient(server.Base) { Origin = PairingTests.ChromeOrigin };
        // The token's last-used time can't be saved; the request is answered all the same.
        Assert.Equal(HttpStatusCode.OK, (await extension.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);
        using (var unpair = await extension.SendAsync(HttpMethod.Delete, "/api/pair/self", null,
                   request => request.Headers.Add("Authorization", "Bearer " + token)))
            Assert.Equal(HttpStatusCode.InternalServerError, unpair.StatusCode);
        // Nothing was removed.
        Assert.Equal(HttpStatusCode.OK, (await extension.IpcAsync(MessageTypes.HostPing, new { }, token)).Status);

        Directory.Delete(authFile + ".tmp");
        using (var unpair = await extension.SendAsync(HttpMethod.Delete, "/api/pair/self", null,
                   request => request.Headers.Add("Authorization", "Bearer " + token)))
            Assert.Equal(HttpStatusCode.NoContent, unpair.StatusCode);
    }
}
