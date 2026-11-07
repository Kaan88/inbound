using System.Security.Cryptography;
using System.Text;
using DownloadManager.Runtime.Remote;

namespace DownloadManager.Integration.Tests;

public sealed class TorrentMetadataTests
{
    private const string Pieces = "20:aaaaaaaaaaaaaaaaaaaa";

    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);

    private static string Sha1Hex(string text) => Convert.ToHexString(SHA1.HashData(Bytes(text))).ToLowerInvariant();

    private static string SingleFileInfo(string name = "file.bin", long length = 1234)
        => $"d6:lengthi{length}e4:name{name.Length}:{name}12:piece lengthi16384e6:pieces{Pieces}e";

    private static string Wrap(string info, string before = "8:announce17:http://t.invalid/", string after = "")
        => $"d{before}4:info{info}{after}e";

    private static string Refused(string text)
    {
        Assert.False(TorrentMetadata.TryParse(Bytes(text), out var metadata, out var problem));
        Assert.Null(metadata);
        Assert.False(string.IsNullOrWhiteSpace(problem));
        return problem!;
    }

    [Fact]
    public void TheHashCoversExactlyTheRawInfoDictionary()
    {
        var info = SingleFileInfo();
        Assert.True(TorrentMetadata.TryParse(Bytes(Wrap(info)), out var metadata, out var problem), problem);
        Assert.Equal(Sha1Hex(info), metadata!.InfoHash);
        Assert.Equal("file.bin", metadata.Name);
        Assert.Equal(1234, metadata.TotalBytes);
        Assert.Equal(1, metadata.FileCount);
    }

    [Fact]
    public void KeysItDoesNotKnowInsideInfoStayInTheHash()
    {
        var info = $"d6:lengthi1e4:name1:a12:piece lengthi1e6:pieces{Pieces}7:privatei1e6:sourcel1:x1:yee";
        Assert.True(TorrentMetadata.TryParse(Bytes(Wrap(info)), out var metadata, out var problem), problem);
        Assert.Equal(Sha1Hex(info), metadata!.InfoHash);
    }

    [Fact]
    public void AMultiFileTorrentAddsUpItsFiles()
    {
        var info = "d5:filesl" +
                   "d6:lengthi10e4:pathl3:sub5:a.bineed6:lengthi20e4:pathl3:sub5:b.bineee" +
                   $"4:name4:pack12:piece lengthi16384e6:pieces{Pieces}e";
        Assert.True(TorrentMetadata.TryParse(Bytes(Wrap(info)), out var metadata, out var problem), problem);
        Assert.Equal(Sha1Hex(info), metadata!.InfoHash);
        Assert.Equal("pack", metadata.Name);
        Assert.Equal(30, metadata.TotalBytes);
        Assert.Equal(2, metadata.FileCount);
    }

    [Fact]
    public void AMultiFileTorrentListsItsFilesInsideItsOwnFolder()
    {
        var info = "d5:filesl" +
                   "d6:lengthi10e4:pathl3:sub5:a.bineed6:lengthi20e4:pathl5:b.bineee" +
                   $"4:name4:pack12:piece lengthi16384e6:pieces{Pieces}e";
        Assert.True(TorrentMetadata.TryParse(Bytes(Wrap(info)), out var metadata, out var problem), problem);
        Assert.Equal([new TorrentFileEntry("pack/sub/a.bin", 10), new TorrentFileEntry("pack/b.bin", 20)], metadata!.Files);
    }

    [Fact]
    public void ASingleFileTorrentListsItsName()
    {
        Assert.True(TorrentMetadata.TryParse(Bytes(Wrap(SingleFileInfo())), out var metadata, out var problem), problem);
        Assert.Equal([new TorrentFileEntry("file.bin", 1234)], metadata!.Files);
    }

    [Fact]
    public void AFilesUtf8PathIsTheOneListed()
    {
        // An old client's own spelling in "path", and the one everything shows in "path.utf-8": 7 bytes for 5 letters.
        var info = "d5:filesl" +
                   "d6:lengthi10e4:pathl5:old.be10:path.utf-8l7:été.bee" +
                   $"e4:name4:pack12:piece lengthi16384e6:pieces{Pieces}e";
        Assert.True(TorrentMetadata.TryParse(Encoding.UTF8.GetBytes(Wrap(info)), out var metadata, out var problem), problem);
        Assert.Equal([new TorrentFileEntry("pack/été.b", 10)], metadata!.Files);
    }

    [Fact]
    public void PaddingFilesAreCountedButNotListed()
    {
        var info = "d5:filesl" +
                   "d6:lengthi10e4:pathl5:a.bineed4:attr1:p6:lengthi6e4:pathl4:.pad1:6eed6:lengthi20e4:pathl5:b.bineee" +
                   $"4:name4:pack12:piece lengthi16384e6:pieces{Pieces}e";
        Assert.True(TorrentMetadata.TryParse(Bytes(Wrap(info)), out var metadata, out var problem), problem);
        Assert.Equal(3, metadata!.FileCount);
        Assert.Equal(36, metadata.TotalBytes);
        Assert.Equal(["pack/a.bin", "pack/b.bin"], metadata.Files.Select(file => file.Path));
    }

    [Fact]
    public void APathOfSomethingOtherThanTextIsRefused()
        => Refused(Wrap($"d5:filesld6:lengthi1e4:pathli1eeee4:name1:a12:piece lengthi1e6:pieces{Pieces}e"));

    [Fact]
    public void BinaryInStringsSurvivesInTheHash()
    {
        var pieces = "20:" + new string('\xFF', 20);
        var info = $"d6:lengthi1e4:name1:a12:piece lengthi1e6:pieces{pieces}e";
        Assert.True(TorrentMetadata.TryParse(Bytes(Wrap(info)), out var metadata, out var problem), problem);
        Assert.Equal(Sha1Hex(info), metadata!.InfoHash);
    }

    [Fact]
    public void AnEmptyFileIsSmallButAllowed()
    {
        Assert.True(TorrentMetadata.TryParse(Bytes(Wrap(SingleFileInfo(length: 0))), out var metadata, out _));
        Assert.Equal(0, metadata!.TotalBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("hello")]
    [InlineData("i5e")]
    [InlineData("le")]
    [InlineData("5:hello")]
    [InlineData("<html></html>")]
    public void NotATorrentAtAll(string text) => Refused(text);

    [Theory]
    [InlineData("d4:infod")]              // Runs out in the middle.
    [InlineData("d4:info")]
    [InlineData("d4:infoi5")]
    [InlineData("d4:info99:short")]       // A string longer than what's left.
    [InlineData("d4:info99999999999999999999:x")] // A length that doesn't fit any number.
    [InlineData("d4:infole")]             // Info must be a dictionary.
    [InlineData("d4:infoi1ee")]
    [InlineData("d4:infoi01ee")]          // Leading zero.
    [InlineData("d4:infoi-0ee")]          // Negative zero.
    [InlineData("d4:infoiee")]            // Empty integer.
    [InlineData("d4:infoi1xee")]
    [InlineData("d04:infodee")]           // Leading zero in a length.
    [InlineData("d4:infodee")]            // Nothing useful in info.
    [InlineData("de")]                    // No info at all.
    [InlineData("di1e4:infoe")]           // A key must be a string.
    public void MalformedInputIsRefused(string text) => Refused(text);

    [Fact]
    public void BytesAfterTheEndAreRefused()
        => Assert.Contains("after", Refused(Wrap(SingleFileInfo()) + "x"));

    [Fact]
    public void ADuplicateKeyIsRefused()
    {
        var info = SingleFileInfo();
        Assert.Contains("twice", Refused($"d4:info{info}4:info{info}e"));
    }

    [Fact]
    public void UnsortedKeysAreRefused()
    {
        var info = SingleFileInfo();
        Assert.Contains("order", Refused($"d4:info{info}8:announce17:http://t.invalid/e"));
    }

    [Theory]
    [InlineData("d6:lengthi1e12:piece lengthi1e6:pieces20:aaaaaaaaaaaaaaaaaaaae")] // No name.
    [InlineData("d6:lengthi1e4:name1:a6:pieces20:aaaaaaaaaaaaaaaaaaaae")]            // No piece length.
    [InlineData("d6:lengthi1e4:name1:a12:piece lengthi1ee")]                         // No pieces.
    [InlineData("d4:name1:a12:piece lengthi1e6:pieces20:aaaaaaaaaaaaaaaaaaaae")]     // Neither length nor files.
    [InlineData("d6:lengthi-1e4:name1:a12:piece lengthi1e6:pieces20:aaaaaaaaaaaaaaaaaaaae")] // Negative length.
    [InlineData("d6:lengthi1e4:name1:a12:piece lengthi0e6:pieces20:aaaaaaaaaaaaaaaaaaaae")]  // Zero piece length.
    [InlineData("d6:lengthi1e4:name1:a12:piece lengthi1e6:pieces19:aaaaaaaaaaaaaaaaaaae")]   // Pieces not in twenties.
    [InlineData("d5:filesle4:name1:a12:piece lengthi1e6:pieces20:aaaaaaaaaaaaaaaaaaaae")]    // Empty file list.
    [InlineData("d5:filesld6:lengthi1eee4:name1:a12:piece lengthi1e6:pieces20:aaaaaaaaaaaaaaaaaaaae")]   // A file with no path.
    [InlineData("d5:filesld4:pathl1:aeee4:name1:a12:piece lengthi1e6:pieces20:aaaaaaaaaaaaaaaaaaaae")]   // A file with no length.
    [InlineData("d5:filesi1e4:name1:a12:piece lengthi1e6:pieces20:aaaaaaaaaaaaaaaaaaaae")]   // Files of the wrong type.
    public void AnInfoDictionaryMissingWhatItNeedsIsRefused(string info) => Refused(Wrap(info));

    [Fact]
    public void AnEnormousIntegerInAKeyWeDoNotReadIsFine()
    {
        var info = $"d6:lengthi1e4:name1:a12:piece lengthi1e6:pieces{Pieces}5:zzzzzi99999999999999999999999ee";
        Assert.True(TorrentMetadata.TryParse(Bytes(Wrap(info)), out _, out var problem), problem);
    }

    [Fact]
    public void ALengthThatOverflowsIsRefused()
    {
        var info = $"d6:lengthi99999999999999999999e4:name1:a12:piece lengthi1e6:pieces{Pieces}e";
        Refused(Wrap(info));
    }

    [Fact]
    public void ALengthsTotalThatOverflowsIsRefused()
    {
        var big = long.MaxValue;
        var info = $"d5:filesld6:lengthi{big}e4:pathl1:aeed6:lengthi{big}e4:pathl1:beee4:name1:a12:piece lengthi1e6:pieces{Pieces}e";
        Refused(Wrap(info));
    }

    [Fact]
    public void ANestingBombIsRefusedWithoutOverflowingTheStack()
    {
        var bomb = new string('l', 1_000_000) + new string('e', 1_000_000);
        Assert.Contains("nested", Refused($"d4:info{bomb}e"));
    }

    [Fact]
    public void AFileOverTheLimitIsRefusedBeforeParsing()
    {
        var huge = new byte[TorrentMetadata.MaxFileBytes + 1];
        Assert.False(TorrentMetadata.TryParse(huge, out _, out var problem));
        Assert.Contains("too large", problem);
    }

    [Fact]
    public void AVersion2OnlyTorrentHasNoHashThatCanBeFoundAgain()
    {
        var info = "d9:file treed1:ad0:d6:lengthi1eeee12:meta versioni2e4:name1:a12:piece lengthi16384ee";
        Assert.Contains("version 2", Refused(Wrap(info)));
    }

    [Fact]
    public void AHybridTorrentUsesItsVersion1Hash()
    {
        var info = $"d9:file treede6:lengthi1e12:meta versioni2e4:name1:a12:piece lengthi16384e6:pieces{Pieces}e";
        Assert.True(TorrentMetadata.TryParse(Bytes(Wrap(info)), out var metadata, out var problem), problem);
        Assert.Equal(Sha1Hex(info), metadata!.InfoHash);
    }

    [Fact]
    public void ANameThatIsNotTextStillGivesAHash()
    {
        var info = "d6:lengthi1e4:name2:ÿþ" + $"12:piece lengthi1e6:pieces{Pieces}e";
        Assert.True(TorrentMetadata.TryParse(Bytes(Wrap(info)), out var metadata, out var problem), problem);
        Assert.Equal(Sha1Hex(info), metadata!.InfoHash);
        Assert.False(string.IsNullOrEmpty(metadata.Name));
    }

    [Fact]
    public void RefusalsNeverQuoteTheFile()
    {
        var problem = Refused("d8:passkey12:secret-value4:infoi1ee");
        Assert.DoesNotContain("secret-value", problem);
    }
}
