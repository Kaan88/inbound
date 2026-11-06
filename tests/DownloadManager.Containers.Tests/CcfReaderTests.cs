using System.Text;
using static DownloadManager.Containers.Tests.ContainerFixtures;

namespace DownloadManager.Containers.Tests;

public class CcfReaderTests
{
    private static readonly string FullXml = CryptLoadXml(
        PackageXml("First package", Password, "A comment",
            DownloadXml(UrlA, "one.rar", "1048576"),
            DownloadXml(UrlB, "two.rar", "2097152")),
        PackageXml("Second package", null, null, DownloadXml(UrlC, "three.zip")));

    public static TheoryData<string, byte[]> AllVersions()
    {
        var data = new TheoryData<string, byte[]>
        {
            { CcfFormatConstants.Version30, Ccf30(FullXml) },
            { CcfFormatConstants.Version50, Ccf50(FullXml) },
        };

        for (var pair = 0; pair < CcfFormatConstants.Legacy.Count; pair++)
        {
            data.Add(CcfFormatConstants.Version07To10, CcfLegacy(FullXml, pair));
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllVersions))]
    public void Reads_every_version_with_packages_and_metadata(string version, byte[] data)
    {
        var result = ContainerReader.Read(data, "links.ccf");

        Assert.Equal(ContainerFormat.Ccf, result.Format);
        Assert.Equal(version, result.FormatVersion);
        Assert.Empty(result.Problems);

        Assert.Equal(2, result.Packages.Count);
        var first = result.Packages[0];
        Assert.Equal("First package", first.Name);
        Assert.Equal([Password], first.ArchivePasswordCandidates);
        Assert.Equal("A comment", first.Comment);
        Assert.Equal("Second package", result.Packages[1].Name);
        Assert.Empty(result.Packages[1].ArchivePasswordCandidates);

        Assert.Equal([UrlA, UrlB, UrlC], result.Links.Select(l => l.Url));
        Assert.Same(first, result.Links[0].Package);
        Assert.Same(result.Packages[1], result.Links[2].Package);
        Assert.Equal("one.rar", result.Links[0].FileName);
        Assert.Equal(1048576, result.Links[0].SizeBytes);
        Assert.Null(result.Links[2].SizeBytes);
    }

    [Theory]
    [MemberData(nameof(AllVersions))]
    public void Is_detected_by_content_without_a_name(string version, byte[] data)
    {
        var result = ContainerReader.Read(data);

        Assert.Equal(ContainerFormat.Ccf, result.Format);
        Assert.Equal(version, result.FormatVersion);
        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(128)]
    [InlineData(1000)]
    public void Ccf30_honours_the_length_of_the_last_block(int extra)
    {
        var xml = CryptLoadXml(PackageXml("P", null, null, DownloadXml(UrlA)));
        var padded = xml + new string(' ', extra);

        var result = ContainerReader.Read(Ccf30(padded, key: (uint)(0x12345678 + extra)), "links.ccf");

        Assert.Equal(UrlA, Assert.Single(result.Links).Url);
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(1u)]
    [InlineData(0xFFFFFFFFu)]
    [InlineData(0x80000000u)]
    [InlineData(0xAAAAAAAAu)]
    public void Ccf30_works_with_edge_case_keys(uint key)
    {
        var result = ContainerReader.Read(Ccf30(FullXml, key), "links.ccf");
        Assert.Equal(3, result.Links.Count);
    }

    [Fact]
    public void Ccf30_data_is_really_transformed_and_not_stored_as_plaintext()
    {
        var data = Ccf30(FullXml);
        Assert.Equal(-1, data.AsSpan(11).IndexOf("CryptLoad"u8));
    }

    [Fact]
    public void Password_is_a_candidate_on_the_package_and_links_carry_no_password_field()
    {
        var result = ContainerReader.Read(CcfLegacy(FullXml), "links.ccf");

        Assert.Equal(Password, Assert.Single(result.Packages[0].ArchivePasswordCandidates));
        Assert.DoesNotContain(typeof(ContainerLink).GetProperties(), p => p.Name.Contains("Password", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Password_keeps_its_surrounding_spaces_and_blank_is_absent()
    {
        var xml = CryptLoadXml(
            PackageXml("A", " spaced pw ", null, DownloadXml(UrlA)),
            PackageXml("B", "   ", null, DownloadXml(UrlB)));

        var result = ContainerReader.Read(CcfLegacy(xml), "links.ccf");

        Assert.Equal(" spaced pw ", Assert.Single(result.Packages[0].ArchivePasswordCandidates));
        Assert.Empty(result.Packages[1].ArchivePasswordCandidates);
    }

    [Fact]
    public void Mixed_metadata_with_names_missing_and_links_outside_packages_is_read()
    {
        var xml = CryptLoadXml(
            $"<url>{Escape(UrlC)}</url>",
            PackageXml(null, Password, null, DownloadXml(UrlA, "..\\..\\evil.exe", "not a number")),
            PackageXml("Only a name", null, null),
            "<Package><Download><URL>" + UrlB + "</URL><FILENAME>b.rar</FILENAME><FILESIZE>-5</FILESIZE></Download></Package>",
            "<Package/>");

        var result = ContainerReader.Read(CcfLegacy(xml), "links.ccf");

        Assert.Empty(result.Problems);
        Assert.Equal(3, result.Links.Count);

        var loose = result.Links.Single(l => l.Url == UrlC);
        Assert.Null(loose.Package);

        var inUnnamed = result.Links.Single(l => l.Url == UrlA);
        Assert.Null(inUnnamed.Package!.Name);
        Assert.Equal([Password], inUnnamed.Package.ArchivePasswordCandidates);
        Assert.Equal("evil.exe", inUnnamed.FileName);
        Assert.Null(inUnnamed.SizeBytes);

        var upper = result.Links.Single(l => l.Url == UrlB);
        Assert.Equal("b.rar", upper.FileName);
        Assert.Null(upper.SizeBytes);

        Assert.Contains(result.Packages, p => p.Name == "Only a name");
        Assert.Equal(3, result.Packages.Count);
    }

    [Fact]
    public void Url_attribute_alone_is_enough_and_a_blank_child_does_not_erase_it()
    {
        var xml = CryptLoadXml($"<Package name=\"P\"><Download Url=\"{UrlA}\"/><Download Url=\"{UrlB}\"><Url></Url></Download></Package>");

        var result = ContainerReader.Read(CcfLegacy(xml), "links.ccf");

        Assert.Equal([UrlA, UrlB], result.Links.Select(l => l.Url));
    }

    [Fact]
    public void Hostile_file_names_are_reduced_to_a_safe_segment_or_dropped()
    {
        Assert.Equal("evil.exe", ContainerText.CleanFileName("../../evil.exe"));
        Assert.Equal("evil.exe", ContainerText.CleanFileName(@"C:\Windows\evil.exe"));
        Assert.Null(ContainerText.CleanFileName(".."));
        Assert.Null(ContainerText.CleanFileName("a/.."));
        Assert.Null(ContainerText.CleanFileName("bad\u0001name"));
        Assert.Null(ContainerText.CleanFileName("   "));
    }

    [Fact]
    public void Invalid_url_entries_are_reported_without_quoting_them_and_others_survive()
    {
        var xml = CryptLoadXml(PackageXml("P", null, null, DownloadXml("not-a-url-secret-text"), DownloadXml(UrlA)));

        var result = ContainerReader.Read(CcfLegacy(xml), "links.ccf");

        Assert.Equal(UrlA, Assert.Single(result.Links).Url);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(ContainerProblemKind.InvalidEntry, problem.Kind);
        Assert.DoesNotContain("secret-text", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_scheme_this_app_cannot_use_is_kept_for_the_classification_stage()
    {
        var xml = CryptLoadXml(PackageXml("P", null, null, DownloadXml("ed2k://file|a|1|ABC|/"), DownloadXml("magnet:?xt=urn:btih:abc")));

        var result = ContainerReader.Read(CcfLegacy(xml), "links.ccf");

        Assert.Equal(2, result.Links.Count);
        Assert.Empty(result.Problems);
    }

    [Theory]
    [InlineData("https://a.test/x", true)]
    [InlineData("HTTP://a.test/x y", true)]
    [InlineData("magnet:?xt=urn:btih:abc", true)]
    [InlineData("ed2k://file|a|1|ABC|/", true)]
    [InlineData("", false)]
    [InlineData("no scheme here", false)]
    [InlineData(@"C:\Windows\win.ini", false)]
    [InlineData("note: just words", false)]
    [InlineData("1ab://a.test", false)]
    [InlineData("http:", false)]
    [InlineData("ht tp://a.test", false)]
    [InlineData("https://a.test/\u0001", false)]
    [InlineData("https://a.test/\uFFFD", false)]
    public void Url_check_accepts_scheme_shaped_text_only(string value, bool expected)
    {
        Assert.Equal(expected, ContainerText.IsUrl(value));
    }

    [Theory]
    [InlineData("""<!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]><CryptLoad><url>&xxe;</url></CryptLoad>""")]
    [InlineData("""<!DOCTYPE lolz [<!ENTITY a "aaaaaaaaaa"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;"><!ENTITY c "&b;&b;&b;&b;&b;&b;&b;&b;&b;&b;">]><CryptLoad><url>https://x.test/&c;</url></CryptLoad>""")]
    [InlineData("""<?xml version="1.0"?><!doctype x SYSTEM "http://169.254.169.254/x.dtd"><CryptLoad><url>https://x.test/a</url></CryptLoad>""")]
    public void A_dtd_inside_the_payload_is_refused_with_no_links(string xml)
    {
        foreach (var data in new[] { CcfLegacy(xml), Ccf30(xml), Ccf50(xml) })
        {
            var result = ContainerReader.Read(data, "links.ccf");

            Assert.Equal(ContainerFormat.Ccf, result.Format);
            Assert.False(result.Succeeded);
            Assert.Equal(ContainerProblemKind.UnsafeXml, Assert.Single(result.Problems).Kind);
        }
    }

    [Fact]
    public void An_undefined_entity_in_a_link_does_not_resolve_anything()
    {
        var xml = $"<CryptLoad><url>{UrlA}</url><url>&undefined;</url></CryptLoad>";

        var result = ContainerReader.Read(CcfLegacy(xml), "links.ccf");

        Assert.Contains(result.Links, l => l.Url == UrlA);
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.Malformed);
    }

    [Fact]
    public void Damaged_xml_recovers_links_by_searching_and_never_quotes_the_input_in_the_problem()
    {
        var xml = $"<CryptLoad><Package name=\"P\"><Options><Passwort>{Password}</Passwort></Options><Download><Url>{UrlA}</Url></Download><bad attr=><url>{UrlB}</url>";

        var result = ContainerReader.Read(CcfLegacy(xml), "links.ccf");

        Assert.Contains(result.Links, l => l.Url == UrlB);
        Assert.Contains(result.Links, l => l.Url == UrlA);
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.Malformed);
        Assert.All(result.Problems, p =>
        {
            Assert.DoesNotContain("example.test", p.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, p.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void Text_with_the_marker_and_urls_but_no_xml_structure_still_gives_links()
    {
        var text = $"CryptLoad junk <url>{UrlA}</url> & more <URL>{UrlB}</URL> <<<";

        var result = ContainerReader.Read(CcfLegacy(text), "links.ccf");

        Assert.Equal([UrlA, UrlB], result.Links.Select(l => l.Url));
    }

    [Fact]
    public void Filler_bytes_after_the_document_do_not_disturb_reading()
    {
        var xml = Encoding.UTF8.GetBytes(FullXml);
        var padded = xml.Concat(new byte[] { 0x07, 0x00, 0xFF, 0x01 }).Concat(new byte[32 - ((xml.Length + 4) % 16)]).ToArray();
        padded = padded[..(padded.Length / 16 * 16)];

        var result = ContainerReader.Read(CcfLegacyBytes(padded), "links.ccf");

        Assert.Equal(3, result.Links.Count);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Filler_holding_a_closing_bracket_still_keeps_every_link()
    {
        var xml = Encoding.UTF8.GetBytes(FullXml);
        var padded = xml.Concat(new byte[] { 0x01, (byte)'>', 0x02 }).Concat(new byte[32]).ToArray();
        padded = padded[..(padded.Length / 16 * 16)];

        var result = ContainerReader.Read(CcfLegacyBytes(padded), "links.ccf");

        Assert.Equal(3, result.Links.Count);
        Assert.Equal(2, result.Packages.Count);
    }

    [Fact]
    public void Latin1_payload_keeps_a_password_intact()
    {
        var xml = CryptLoadXml(PackageXml("P", "pässwört", null, DownloadXml(UrlA)));
        var plain = Encoding.Latin1.GetBytes(xml);
        var padded = plain.Concat(new byte[16 - (plain.Length % 16)]).ToArray();

        var result = ContainerReader.Read(CcfLegacyBytes(padded), "links.ccf");

        Assert.Equal("pässwört", Assert.Single(result.Packages[0].ArchivePasswordCandidates));
    }

    [Fact]
    public void Deep_nesting_stops_at_the_limit_without_recursion_trouble()
    {
        const int Depth = 200_000;
        var builder = new StringBuilder("<CryptLoad>");
        builder.Append($"<url>{UrlA}</url>");
        for (var i = 0; i < Depth; i++)
        {
            builder.Append("<a>");
        }

        var result = ContainerReader.Read(CcfLegacy(builder.ToString()), "links.ccf");

        Assert.Equal(UrlA, Assert.Single(result.Links).Url);
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.Malformed);
    }

    [Fact]
    public void Decrypted_without_marker_is_a_decryption_failure()
    {
        var xml = "<Other><url>https://files.example.test/x</url></Other>";

        foreach (var data in new[] { CcfLegacy(xml), Ccf30(xml), Ccf50(xml) })
        {
            var result = ContainerReader.Read(data, "links.ccf");

            Assert.False(result.Succeeded);
            Assert.Equal(ContainerProblemKind.DecryptionFailed, Assert.Single(result.Problems).Kind);
        }
    }

    [Fact]
    public void Marker_but_no_links_is_reported()
    {
        var result = ContainerReader.Read(CcfLegacy("<CryptLoad></CryptLoad>"), "links.ccf");

        Assert.Equal(ContainerFormat.Ccf, result.Format);
        Assert.False(result.Succeeded);
        Assert.Equal(ContainerProblemKind.Malformed, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Marker_with_no_markup_is_reported()
    {
        var result = ContainerReader.Read(CcfLegacy("CryptLoad and nothing else"), "links.ccf");

        Assert.False(result.Succeeded);
        Assert.Equal(ContainerProblemKind.Malformed, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Ccf30_truncated_inside_a_block_is_reported()
    {
        var data = Ccf30(FullXml);

        var result = ContainerReader.Read(data[..^1], "links.ccf");

        Assert.Equal(ContainerFormat.Ccf, result.Format);
        Assert.Equal(ContainerProblemKind.Truncated, Assert.Single(result.Problems).Kind);
    }

    [Theory]
    [InlineData(6)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(74)]
    public void Ccf30_with_a_short_body_is_reported(int length)
    {
        var result = ContainerReader.Read(Ccf30(FullXml)[..length], "whatever");

        Assert.False(result.Succeeded);
        Assert.Equal(ContainerProblemKind.Truncated, Assert.Single(result.Problems).Kind);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(200)]
    [InlineData(255)]
    public void Ccf30_with_an_impossible_last_block_length_is_reported(byte value)
    {
        var data = Ccf30(FullXml);
        data[Ccf30Cipher.LastBlockLengthOffset] = value;

        var result = ContainerReader.Read(data, "links.ccf");

        Assert.Equal(ContainerProblemKind.Malformed, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Cbc_ciphertext_truncated_inside_a_block_is_reported()
    {
        var result = ContainerReader.Read(CcfLegacy(FullXml)[..^3], "links.ccf");

        Assert.Equal(ContainerFormat.Ccf, result.Format);
        Assert.Equal(ContainerProblemKind.Truncated, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Cbc_ciphertext_truncated_on_a_block_boundary_keeps_what_can_be_read()
    {
        var data = CcfLegacy(FullXml);

        var result = ContainerReader.Read(data[..(data.Length / 16 / 2 * 16)], "links.ccf");

        Assert.Equal(ContainerFormat.Ccf, result.Format);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Random_bytes_named_ccf_fail_decryption()
    {
        var random = new byte[1600];
        new Random(5).NextBytes(random);

        var result = ContainerReader.Read(random, "links.ccf");

        Assert.Equal(ContainerFormat.Ccf, result.Format);
        Assert.Equal(ContainerProblemKind.DecryptionFailed, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Limits_on_links_packages_and_field_length_are_enforced()
    {
        var manyLinks = CryptLoadXml(PackageXml("P", null, null,
            DownloadXml(UrlA), DownloadXml(UrlB), DownloadXml(UrlC)));
        var linkLimited = ContainerReader.Read(CcfLegacy(manyLinks), "links.ccf", new ContainerLimits { MaxLinks = 2 });
        Assert.Equal(2, linkLimited.Links.Count);
        Assert.Contains(linkLimited.Problems, p => p.Kind == ContainerProblemKind.TooManyEntries);

        var manyPackages = CryptLoadXml(
            PackageXml("A", null, null, DownloadXml(UrlA)),
            PackageXml("B", null, null, DownloadXml(UrlB)),
            PackageXml("C", null, null, DownloadXml(UrlC)));
        var packageLimited = ContainerReader.Read(CcfLegacy(manyPackages), "links.ccf", new ContainerLimits { MaxPackages = 1 });
        Assert.Single(packageLimited.Packages);
        Assert.Contains(packageLimited.Problems, p => p.Kind == ContainerProblemKind.TooManyEntries);

        var longField = CryptLoadXml($"<url>https://files.example.test/{new string('a', 20_000)}</url><url>{UrlA}</url>");
        var fieldLimited = ContainerReader.Read(CcfLegacy(longField), "links.ccf");
        Assert.Equal(UrlA, Assert.Single(fieldLimited.Links).Url);
        Assert.Equal(ContainerProblemKind.InvalidEntry, Assert.Single(fieldLimited.Problems).Kind);
    }

    [Fact]
    public void Constants_have_the_shape_aes256_cbc_needs()
    {
        var pairs = CcfFormatConstants.Legacy.Append(CcfFormatConstants.Ccf50Outer).Append(CcfFormatConstants.Ccf50Inner).ToArray();

        Assert.Equal(5, pairs.Length);
        Assert.All(pairs, pair =>
        {
            Assert.Equal(32, pair.Key.Length);
            Assert.Equal(16, pair.Iv.Length);
        });
    }

    private static byte[] CcfLegacyBytes(byte[] plain)
    {
        using var aes = System.Security.Cryptography.Aes.Create();
        var pair = CcfFormatConstants.Legacy[0];
        aes.Key = pair.Key;
        return aes.EncryptCbc(plain, pair.Iv, System.Security.Cryptography.PaddingMode.None);
    }
}
