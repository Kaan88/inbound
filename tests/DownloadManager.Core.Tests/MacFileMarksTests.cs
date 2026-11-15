using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

/// <summary>MAC.6: the quarantine and "Where from" attributes a Mac download gets.</summary>
public sealed class MacFileMarksTests
{
    [Fact]
    public void WhereFromsIsABinaryPlistArrayOfTheSite()
    {
        var bytes = MacFileMarks.WhereFroms(["https://a.io/"]);
        byte[] expected =
        [
            .. "bplist00"u8.ToArray(),
            0xA1, 0x01, // an array of one, its object 1
            0x5D, .. "https://a.io/"u8.ToArray(), // an ASCII string of 13
            0x00, 0x08, 0x00, 0x0A, // the offsets of objects 0 and 1
            0, 0, 0, 0, 0, 0, 2, 1, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 24,
        ];
        Assert.Equal(expected, bytes);
    }

    [Fact]
    public void ALongerSiteHasATwoByteLength()
    {
        var site = "https://downloads.example.com/";
        var bytes = MacFileMarks.WhereFroms([site]);
        Assert.Equal(new byte[] { 0x5F, 0x11, 0x00, (byte)site.Length }, bytes[10..14]);
        Assert.Throws<ArgumentException>(() => MacFileMarks.WhereFroms(["https://ü.example/"]));
    }

    [Fact]
    public void TheQuarantineNamesInboundAndTheTime()
    {
        var value = MacFileMarks.Quarantine(DateTimeOffset.FromUnixTimeSeconds(0x5f000000), Guid.Parse("01234567-89ab-cdef-0123-456789abcdef"));
        Assert.Equal("0081;5f000000;Inbound;01234567-89AB-CDEF-0123-456789ABCDEF", value);
    }
}
