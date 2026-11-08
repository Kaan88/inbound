using System.Text;
using DownloadManager.Persistence;

namespace DownloadManager.Server.Tests;

public sealed class SecretKeyFileTests : IDisposable
{
    private static readonly byte[] Purpose = Encoding.UTF8.GetBytes("Test.Purpose.v1");
    private static readonly byte[] OtherPurpose = Encoding.UTF8.GetBytes("Test.Other.v1");
    private readonly string _work = ServerHarness.NewWork();

    private string KeyFile => Path.Combine(_work, "data", AesGcmSecretProtector.DefaultFileName);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_work, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // left for the next clean
        }
    }

    [Fact]
    public void ASecretRoundTripsWithItsPurposeOnlyAndNeverReadsAsItself()
    {
        var protector = AesGcmSecretProtector.OpenOrCreate(KeyFile);
        var sealedValue = protector.Protect("hunter2-not-real", Purpose);

        Assert.StartsWith(AesGcmSecretProtector.BlobPrefix, sealedValue);
        Assert.DoesNotContain("hunter2", sealedValue);
        Assert.Equal("hunter2-not-real", protector.Unprotect(sealedValue, Purpose));
        Assert.Null(protector.Unprotect(sealedValue, OtherPurpose));
        // Plaintext is never taken for a sealed value, and neither is a damaged one.
        Assert.Null(protector.Unprotect("hunter2-not-real", Purpose));
        Assert.Null(protector.Unprotect(sealedValue[..^4] + "AAAA", Purpose));
        Assert.Null(protector.Unprotect(AesGcmSecretProtector.BlobPrefix + "not base64!", Purpose));
        // Two seals of one value differ (a new nonce each time).
        Assert.NotEqual(sealedValue, protector.Protect("hunter2-not-real", Purpose));
    }

    [Fact]
    public void TheKeyFileIs32BytesOwnerOnlyAndReusedOnTheNextStart()
    {
        var first = AesGcmSecretProtector.OpenOrCreate(KeyFile);
        var sealedValue = first.Protect("value", Purpose);

        Assert.Equal(AesGcmSecretProtector.KeyBytes, new FileInfo(KeyFile).Length);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(KeyFile)!, "*.tmp"));

        var second = AesGcmSecretProtector.OpenOrCreate(KeyFile);
        Assert.Equal("value", second.Unprotect(sealedValue, Purpose));
    }

    [Fact]
    public void AnotherKeyFileCantOpenTheSecretAndSaysWhyOnce()
    {
        var sealedValue = AesGcmSecretProtector.OpenOrCreate(KeyFile).Protect("value", Purpose);
        var warnings = new List<string>();
        var other = AesGcmSecretProtector.OpenOrCreate(Path.Combine(_work, "elsewhere", AesGcmSecretProtector.DefaultFileName), warnings.Add);

        Assert.Null(other.Unprotect(sealedValue, Purpose));
        Assert.Null(other.Unprotect(sealedValue, Purpose));
        var warning = Assert.Single(warnings);
        Assert.Contains("another key", warning);
        Assert.DoesNotContain(sealedValue, warning);
    }

    [Fact]
    public void AWindowsBlobIsReportedAndNotRead()
    {
        var warnings = new List<string>();
        var protector = AesGcmSecretProtector.OpenOrCreate(KeyFile, warnings.Add);
        Assert.Null(protector.Unprotect(DpapiSecretProtector.BlobPrefix + "AAAA", Purpose));
        Assert.Contains("DPAPI", Assert.Single(warnings));
    }

    [Fact]
    public void AKeyFileOfTheWrongSizeIsRefusedWithWhatToDo()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(KeyFile)!);
        File.WriteAllBytes(KeyFile, new byte[7]);
        var refused = Assert.Throws<InvalidDataException>(() => AesGcmSecretProtector.OpenOrCreate(KeyFile));
        Assert.Contains("Restore it", refused.Message);
        // Nothing replaced the damaged key, which may still be recoverable.
        Assert.Equal(7, new FileInfo(KeyFile).Length);
    }

    [Fact]
    public void AKeyFileOthersCanReadIsMadeOwnerOnlyAndThatIsSaid()
    {
        if (OperatingSystem.IsWindows()) return;
        AesGcmSecretProtector.OpenOrCreate(KeyFile);
        File.SetUnixFileMode(KeyFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var warnings = new List<string>();

        AesGcmSecretProtector.OpenOrCreate(KeyFile, warnings.Add);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(KeyFile));
        Assert.Contains("owner-only", Assert.Single(warnings));
    }

    [Fact]
    public void TheFacadeSealsWithTheChosenProtectorAndKeepsPurposesApart()
    {
        var before = SecretProtector.Current;
        try
        {
            SecretProtector.Use(AesGcmSecretProtector.OpenOrCreate(KeyFile));
            var login = SecretProtector.Protect("p");
            Assert.True(SecretProtector.IsProtected(login));
            Assert.Equal("p", SecretProtector.Unprotect(login));
            Assert.Null(SecretProtector.UnprotectProxy(login));
            Assert.Null(SecretProtector.UnprotectProviderKey(login));
            Assert.Equal([1, 2, 3], SecretProtector.UnprotectRemoteTorrent(SecretProtector.ProtectRemoteTorrent([1, 2, 3]))!);
        }
        finally
        {
            SecretProtector.Use(before);
        }
    }
}
