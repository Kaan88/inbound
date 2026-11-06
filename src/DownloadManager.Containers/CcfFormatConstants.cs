using System.Text;

namespace DownloadManager.Containers;

// Where these come from: the public mirror of JDownloader's source at
// https://raw.githubusercontent.com/mirror/jdownloader/master/src/org/jdownloader/container/C.java
// which carries a GPLv3 header. No independent public description of CCF was found, so that file is the only
// source for these values and for the CCF 3.0 block rules in Ccf30Cipher. The values were not taken from an
// installed copy of JDownloader.
//
// Why they are here: they are facts about the file format, needed to read the owner's own CCF files. No code from
// that source is used. The readers in this library are a separate implementation written from the format's
// description. Provenance is recorded for the owner to judge; see notes-local/torbox-research-containers-extraction.md.
internal static class CcfFormatConstants
{
    public const string Version30 = "3.0";
    public const string Version07To10 = "0.7-1.0";
    public const string Version50 = "5.0";

    public static ReadOnlySpan<byte> Ccf30Magic => "CCF3.0"u8;

    /// <summary>Every version's plaintext holds this text. It is the only test that decryption worked.</summary>
    public static ReadOnlySpan<byte> DecryptedMarker => "CryptLoad"u8;

    /// <summary>CCF 0.7, 0.8 and 1.0: AES-256-CBC. Which pair belongs to which version is not recorded, so each is tried.</summary>
    public static readonly IReadOnlyList<CcfKeyPair> Legacy =
    [
        new("026900E977C6402442B661329CFE62D6ED21BDEB0CD6321318A8EDC7BC5A6C86", "8CE1173EBAD76E08584B94573926231E"),
        new("171BF8E34C3D0C0C2693FDD2B080423A5B98F4D028A0AF4D82A385D837A8F95F", "9FE95FFF7CA4FC0FCEF25E4F7444AE67"),
        new("5F679C00548737E120E6518A981BD0BA11AF5C719E97502983AD6AA38ED721C3", "E3D153AD609EF7358D66684180C7331A"),
    ];

    /// <summary>CCF 5.0: AES-256-CBC three times over, decrypting with this pair, then the second, then this one again.</summary>
    public static readonly CcfKeyPair Ccf50Outer = new("64E9E143CE4634DA5CD99B0CBFA3002A9A3765E10CB19CFF906DB6A68F95B398", "E0FEABE3F4B13E6F05F4A5A35B7FBDC8");

    public static readonly CcfKeyPair Ccf50Inner = new("FFC9122B34FAE1043087DCA5FAAAAB109414049A6AD2F9F161C7576BE464E48A", "B506B639984C9285ADFAC5B42BAE6F47");
}

internal sealed class CcfKeyPair(string keyHex, string ivHex)
{
    public byte[] Key { get; } = Convert.FromHexString(keyHex);

    public byte[] Iv { get; } = Convert.FromHexString(ivHex);
}
