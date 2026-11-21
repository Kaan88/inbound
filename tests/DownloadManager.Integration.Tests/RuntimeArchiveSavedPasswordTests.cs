using System.Text;
using DownloadManager.Archives.Tests;
using DownloadManager.Contracts;
using DownloadManager.Persistence;

namespace DownloadManager.Integration.Tests;

/// <summary>ANDROID.9 (owner, 2026-11-21): passwords saved for any archive, tried in order before asking, and a set's own delete choice.</summary>
[Collection("runtime-archive")]
public sealed class RuntimeArchiveSavedPasswordTests(ArchiveFileServer fixture)
{
    private const string Secret = "Test-Pw_123 sp";
    private static readonly byte[] Text = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("from the archive\n", 50)));

    private readonly ArchiveFileServer _server = fixture;

    private static string PasswordsFile(ArchiveHarness run) => Path.Combine(run.Paths.Root, "archive", "passwords.json");

    [SevenZipFact]
    public async Task SavedPasswordsAreTriedInOrderBeforeAnyoneIsAsked()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        Assert.True((await run.Archives.AddSavedPasswordAsync("not this one")).Ok);
        var saved = await run.Archives.AddSavedPasswordAsync(Secret);
        Assert.True(saved.Ok, saved.Error);
        Assert.Equal(2, saved.Passwords.Count);

        var archive = run.WriteFile("old.rar", RarBuilder.Rar4Encrypted("data.txt", Text, Secret));
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var done = await run.WaitForStateAsync("completed");

        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("data.txt")));
        Assert.Equal(0, done.SavedPasswordsTried);
        var file = File.ReadAllText(PasswordsFile(run));
        Assert.Contains(SecretProtector.Current.Prefix, file);
        Assert.DoesNotContain(Secret, file);
        Assert.DoesNotContain(Secret, run.StateText());
        Assert.Equal(2, run.Archives.GetSettings().Settings.SavedPasswords);
    }

    [SevenZipFact]
    public async Task WhenNoSavedPasswordOpensItItAsksAndANewSavedOneIsTriedAtOnce()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.AddSavedPasswordAsync("first");
        await run.Archives.AddSavedPasswordAsync("second");

        var archive = run.WriteFile("old.rar", RarBuilder.Rar4Encrypted("data.txt", Text, Secret));
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var refused = await run.WaitForFailureAsync("wrongPassword");
        Assert.True(refused.NeedsPassword);
        Assert.Equal(2, refused.SavedPasswordsTried);
        Assert.Equal(["old.rar"], run.AllFiles());

        // The same one again is not added twice, and changes nothing.
        Assert.Equal(2, (await run.Archives.AddSavedPasswordAsync("first")).Passwords.Count);

        await run.Archives.AddSavedPasswordAsync(Secret);
        await run.WaitForStateAsync("completed");
        Assert.Equal(Text, File.ReadAllBytes(run.PathOf("data.txt")));
    }

    [SevenZipFact]
    public async Task APasswordSavedFromTheSheetJoinsTheListAndTheSetsOwnDeleteChoiceIsUsed()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        var archive = run.WriteFile("old.rar", RarBuilder.Rar4Encrypted("data.txt", Text, Secret));
        await run.Archives.ExtractAsync(new ArchiveExtractRequest { FilePath = archive });
        var needs = await run.WaitForFailureAsync("passwordRequired");
        Assert.False(needs.DeleteArchives);

        var given = await run.Archives.SetPasswordAsync(new ArchiveSetPasswordRequest
        {
            SetId = needs.Id,
            Password = Secret,
            SaveForOtherArchives = true,
            DeleteArchives = true,
        });
        Assert.True(given.Ok, given.Error);
        Assert.True(given.Set!.DeleteArchives);

        var done = await run.WaitForSetAsync(set => set.State == "completed" && set.ArchivesDeleted);
        Assert.True(done.DeleteArchives);
        Assert.False(File.Exists(archive));
        Assert.False(run.Archives.GetSettings().Settings.DeleteArchives);
        Assert.Single(run.Archives.ListSavedPasswords().Passwords);
    }

    [SevenZipFact]
    public async Task TheListOutlivesARestartAndARemovedPasswordIsGone()
    {
        await using var run = await ArchiveHarness.StartAsync(_server);
        await run.Archives.AddSavedPasswordAsync("first");
        var two = await run.Archives.AddSavedPasswordAsync("second");
        var first = two.Passwords[0].Id;

        await run.RestartAsync();
        var listed = run.Archives.ListSavedPasswords();
        Assert.Equal(two.Passwords.Select(p => p.Id), listed.Passwords.Select(p => p.Id));

        var removed = await run.Archives.RemoveSavedPasswordAsync(first);
        Assert.True(removed.Ok, removed.Error);
        Assert.Single(removed.Passwords);
        Assert.False((await run.Archives.RemoveSavedPasswordAsync(first)).Ok);

        Assert.False((await run.Archives.AddSavedPasswordAsync("")).Ok);
        Assert.False((await run.Archives.AddSavedPasswordAsync("line\nbreak")).Ok);
    }
}
