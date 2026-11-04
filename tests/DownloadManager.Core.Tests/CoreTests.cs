using DownloadManager.Contracts;
using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

public sealed class FileNameResolverTests
{
    [Theory]
    [InlineData("attachment; filename=\"report.pdf\"", "report.pdf")]
    [InlineData("attachment; filename=simple.zip", "simple.zip")]
    [InlineData("inline; filename*=UTF-8''na%C3%AFve%20file.txt", "naïve file.txt")]
    [InlineData("attachment; filename=\"../../evil.exe\"", "evil.exe")]
    [InlineData("attachment; filename=\"CON.txt\"", "_CON.txt")]
    public void ParsesAndSanitizes(string header, string expected)
    {
        Assert.Equal(expected, FileNameResolver.FromContentDisposition(header) is { } parsed
            ? FileNameResolver.Sanitize(parsed)
            : "MISSING");
    }

    [Fact]
    public void FallsBackToUrl()
    {
        var name = FileNameResolver.Resolve(null, new Uri("https://example.com/path/file%20name.bin?x=1"));
        Assert.Equal("file name.bin", name);
    }

    [Fact]
    public void RejectsTraversalAndInvalidCharacters()
    {
        Assert.Equal("file.bin", FileNameResolver.Sanitize("..\\..\\file.bin"));
        Assert.Equal("a_b_c_.txt", FileNameResolver.Sanitize("a:b*c?.txt"));
        Assert.Equal("download", FileNameResolver.Sanitize("   "));
    }

    [Fact]
    public void ResolveUniquePathNumbersConflicts()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var first = FileNameResolver.ResolveUniquePath(dir, "file.zip");
            File.WriteAllText(first, "x");
            var second = FileNameResolver.ResolveUniquePath(dir, "file.zip");
            Assert.EndsWith("file (1).zip", second);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public sealed class SpeedEstimatorTests
{
    [Fact]
    public void EstimatesSpeedAndEta()
    {
        var estimator = new SpeedEstimator(TimeSpan.FromSeconds(10));
        var start = DateTimeOffset.UtcNow;
        estimator.Update(0, 1000, start);
        estimator.Update(500, 1000, start.AddSeconds(1));
        var (speed, eta) = estimator.Update(1000, 2000, start.AddSeconds(2));
        Assert.InRange(speed, 400, 600);
        Assert.NotNull(eta);
    }
}

public sealed class DownloadJobTests
{
    [Fact]
    public void CloneCopiesHeadersWithoutSharingDictionary()
    {
        var job = new DownloadJob { Url = "https://example.com/a" };
        job.RequestHeaders["Cookie"] = "a=b";
        var clone = job.Clone();
        clone.RequestHeaders["Cookie"] = "c=d";
        Assert.Equal("a=b", job.RequestHeaders["Cookie"]);
    }

    [Fact]
    public void ProgressFractionHandlesUnknownSize()
    {
        var job = new DownloadJob { ReceivedBytes = 10 };
        Assert.Null(job.ProgressFraction);
        job.TotalBytes = 100;
        Assert.Equal(0.1, job.ProgressFraction);
    }
}

public sealed class JobListFileTests
{
    [Fact]
    public void RoundTripsExportFields()
    {
        var jobs = new[]
        {
            new JobDto
            {
                Url = "https://example.com/a.zip",
                FileName = "a.zip",
                DestinationDirectory = "C:\\Downloads",
                Description = "keep me",
                QueueId = 3,
                RequestedConnections = 4,
                StartMode = "later",
                ConflictPolicy = "overwrite",
                ExpectedSha256 = "abc123",
            },
        };

        var entries = JobListFile.Deserialize(JobListFile.Serialize(jobs));

        var entry = Assert.Single(entries);
        Assert.Equal("https://example.com/a.zip", entry.Url);
        Assert.Equal("a.zip", entry.FileName);
        Assert.Equal("C:\\Downloads", entry.DestinationDirectory);
        Assert.Equal("keep me", entry.Description);
        Assert.Equal(3, entry.QueueId);
        Assert.Equal(4, entry.RequestedConnections);
        Assert.Equal("later", entry.StartMode);
        Assert.Equal("overwrite", entry.ConflictPolicy);
        Assert.Equal("abc123", entry.ExpectedSha256);
    }

    [Fact]
    public void RejectsMalformedJson()
    {
        Assert.Throws<FormatException>(() => JobListFile.Deserialize("{not json"));
    }

    [Fact]
    public void DropsEntriesWithoutUrl()
    {
        Assert.Empty(JobListFile.Deserialize("[{\"url\":\"\"}]"));
    }
}

public sealed class UpdateManifestVerifierTests
{
    [Fact]
    public void VerifiesSignedManifestAndRejectsTampering()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var manifest = new UpdateManifest
        {
            Version = "1.2.3",
            Channel = "stable",
            InstallerUrl = "https://example.com/installer.zip",
            Size = 1234,
            Sha256 = new string('a', 64),
        };
        var signature = rsa.SignData(
            System.Text.Encoding.UTF8.GetBytes(UpdateManifestVerifier.Canonical(manifest)),
            System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        manifest.Signature = Convert.ToBase64String(signature);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();

        Assert.True(UpdateManifestVerifier.Verify(manifest, publicKey));
        manifest.Size++;
        Assert.False(UpdateManifestVerifier.Verify(manifest, publicKey));
    }
}

public sealed class AppSettingsDefaultsTests
{
    [Fact]
    public void DefaultsMatchObservedIdmBaseline()
    {
        var settings = new AppSettings();
        Assert.Equal(8, settings.DefaultConnections);
        Assert.Equal(8, settings.MaxConnectionsPerHost);
        Assert.False(settings.LaunchAtStartup);
        Assert.False(settings.ClipboardMonitoring);
        Assert.True(settings.BrowserIntegrationEnabled);
        Assert.True(settings.NotifyOnCompletion);
        Assert.True(settings.PlayCompletionSound);
        Assert.True(settings.ShowCompletionDialog);
        Assert.True(settings.AutoResumeUnfinishedOnStartup);
        Assert.Equal("stable", settings.UpdateChannel);
        Assert.Equal(24, settings.UpdateCheckIntervalHours);
        Assert.Empty(settings.CategoryFolders);
        Assert.Empty(settings.ConnectionProfiles);
    }
}

public sealed class ShellActionsTests
{
    [Fact]
    public void OpenFileUsesShellExecute()
    {
        var start = ShellActions.OpenFile(@"C:\Downloads\report.pdf");
        Assert.Equal(@"C:\Downloads\report.pdf", start.FileName);
        Assert.True(start.UseShellExecute);
    }

    [Fact]
    public void RevealFileSelectsExistingFile()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-work");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"dm-shell-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, "x");
        try
        {
            var start = ShellActions.RevealFile(path);
            Assert.Equal("explorer.exe", start.FileName);
            Assert.Equal($"/select,\"{path}\"", start.Arguments);
            Assert.True(start.UseShellExecute);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void RevealFileFallsBackToFolderWhenMissing()
    {
        var path = @"C:\Downloads\missing.bin";
        var start = ShellActions.RevealFile(path);
        Assert.Equal("explorer.exe", start.FileName);
        Assert.Equal("\"C:\\Downloads\"", start.Arguments);
        Assert.True(start.UseShellExecute);
    }
}

public sealed class PostDownloadCommandLineTests
{
    [Fact]
    public void PassesFilePathAsSingleArgumentWithoutShell()
    {
        var path = @"C:\Downloads\hook file (1).bin";
        var start = PostDownloadCommandLine.Create(@"C:\Program Files\Scanner\scan.exe", path);

        Assert.False(start.UseShellExecute);
        Assert.Equal(@"C:\Program Files\Scanner\scan.exe", start.FileName);
        Assert.Equal(path, Assert.Single(start.ArgumentList));
    }
}

public sealed class FileCategoriesTests
{
    [Theory]
    [InlineData("archive.zip", "compressed")]
    [InlineData("report.pdf", "documents")]
    [InlineData("song.mp3", "music")]
    [InlineData("setup.exe", "programs")]
    [InlineData("movie.mkv", "video")]
    [InlineData("data.bin", "other")]
    [InlineData("UPPER.MP3", "music")]
    public void MapsExtensions(string fileName, string expected)
    {
        Assert.Equal(expected, FileCategories.For(fileName));
    }
}

public sealed class BatchUrlExpanderTests
{
    [Fact]
    public void ExpandsZeroPaddedNumericRange()
    {
        Assert.Equal(
            new[]
            {
                "https://example.com/file001.zip",
                "https://example.com/file002.zip",
                "https://example.com/file003.zip",
            },
            BatchUrlExpander.Expand("https://example.com/file[001-003].zip"));
    }

    [Fact]
    public void ExpandsDescendingLetters()
    {
        Assert.Equal(
            new[] { "https://example.com/c.txt", "https://example.com/b.txt", "https://example.com/a.txt" },
            BatchUrlExpander.Expand("https://example.com/[c-a].txt"));
    }

    [Fact]
    public void ExpandsCommaListAndCartesianProduct()
    {
        Assert.Equal(
            new[]
            {
                "https://example.com/1/a.bin",
                "https://example.com/1/b.bin",
                "https://example.com/3/a.bin",
                "https://example.com/3/b.bin",
            },
            BatchUrlExpander.Expand("https://example.com/[1,3]/[a-b].bin"));
    }

    [Fact]
    public void ReturnsSingleUrlWithoutRanges()
    {
        Assert.Equal(
            new[] { "https://example.com/file.zip" },
            BatchUrlExpander.Expand("https://example.com/file.zip"));
    }

    [Theory]
    [InlineData("https://example.com/[1-].zip")]
    [InlineData("https://example.com/[a-1].zip")]
    [InlineData("https://example.com/[1,].zip")]
    [InlineData("https://example.com/[1-2.zip")]
    public void RejectsInvalidPatterns(string pattern)
    {
        Assert.Throws<FormatException>(() => BatchUrlExpander.Expand(pattern));
    }

    [Fact]
    public void EnforcesExpansionLimit()
    {
        Assert.Throws<FormatException>(() => BatchUrlExpander.Expand("https://example.com/[1-100].zip", 10));
    }
}

public sealed class ClipboardUrlExtractorTests
{
    [Theory]
    [InlineData("https://example.com/file.zip", "https://example.com/file.zip")]
    [InlineData("Download this: https://example.com/a.bin now.", "https://example.com/a.bin")]
    [InlineData("(https://example.com/a?x=1)", "https://example.com/a?x=1")]
    [InlineData("one\nhttp://example.com/b\nthree", "http://example.com/b")]
    public void FindsFirstHttpUrl(string text, string expected)
    {
        Assert.Equal(expected, ClipboardUrlExtractor.FirstHttpUrl(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("no links here")]
    [InlineData("ftp://example.com/file.zip")]
    [InlineData("javascript:alert(1)")]
    public void RejectsNonHttpText(string text)
    {
        Assert.Null(ClipboardUrlExtractor.FirstHttpUrl(text));
    }
}

public sealed class ShutdownCommandTests
{
    [Theory]
    [InlineData("shutdown", false, "/s /t 30")]
    [InlineData("shutdown", true, "/s /t 30 /f")]
    [InlineData("restart", false, "/r /t 30")]
    [InlineData("restart", true, "/r /t 30 /f")]
    [InlineData("logoff", false, "/l /t 30")]
    [InlineData("bogus", false, "/s /t 30")]
    public void BuildsSafeWindowsArguments(string mode, bool force, string expected)
    {
        Assert.Equal(expected, ShutdownCommand.BuildArguments(mode, force));
    }
}
