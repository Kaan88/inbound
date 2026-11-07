using System.Security.Cryptography;
using System.Text;
using static DownloadManager.Containers.Tests.ContainerFixtures;

namespace DownloadManager.Containers.Tests;

public class RsdfReaderTests
{
    [Fact]
    public void Reads_links_with_no_package_data()
    {
        var result = ContainerReader.Read(Rsdf(UrlA, UrlB, UrlC), "links.rsdf");

        Assert.Equal(ContainerFormat.Rsdf, result.Format);
        Assert.Empty(result.Problems);
        Assert.Empty(result.Packages);
        Assert.Equal([UrlA, UrlB, UrlC], result.Links.Select(l => l.Url));
        Assert.All(result.Links, l =>
        {
            Assert.Null(l.Package);
            Assert.Null(l.FileName);
            Assert.Null(l.SizeBytes);
        });
    }

    [Fact]
    public void Working_register_matches_the_value_published_independently_of_the_key_source()
    {
        Assert.Equal("a3d5a33cb95ac1f5cbdb1ad25cb0a7aa", Convert.ToHexStringLower(RsdfReader.InitialRegister()));
    }

    [Fact]
    public void A_file_encrypted_with_the_register_reset_per_line_fails_on_later_lines()
    {
        // This pins the one register across lines rule: the wrong scheme still decodes line one.
        var data = RsdfWith("\n", resetPerLine: true, $"CCF: {UrlA}", $"CCF: {UrlB}");

        var result = ContainerReader.Read(data, "links.rsdf");

        Assert.Equal(UrlA, Assert.Single(result.Links).Url);
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.InvalidEntry);
    }

    [Fact]
    public void Link_without_the_ccf_prefix_is_accepted()
    {
        var result = ContainerReader.Read(RsdfWith("\n", false, UrlA), "links.rsdf");
        Assert.Equal(UrlA, Assert.Single(result.Links).Url);
    }

    [Fact]
    public void Splits_on_the_alternate_separator_when_it_is_present()
    {
        var lines = RsdfCipherLines(false, $"CCF: {UrlA}", $"CCF: {UrlB}");
        var decoded = new List<byte>(Encoding.ASCII.GetBytes(lines[0])) { 0xDA };
        decoded.AddRange(Encoding.ASCII.GetBytes(lines[1]));

        var result = ContainerReader.Read(ToHexFile(decoded.ToArray()), "links.rsdf");

        Assert.Equal([UrlA, UrlB], result.Links.Select(l => l.Url));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Lower_case_and_wrapped_hex_with_a_bom_and_crlf_lines_are_read()
    {
        var lines = RsdfCipherLines(false, $"CCF: {UrlA}", $"CCF: {UrlB}");
        var hex = Convert.ToHexStringLower(Encoding.ASCII.GetBytes(string.Join("\r\n", lines)));
        var wrapped = "﻿" + hex[..40] + "\r\n" + hex[40..] + "\r\n";

        var result = ContainerReader.Read(Encoding.UTF8.GetBytes(wrapped), "links.rsdf");

        Assert.Equal([UrlA, UrlB], result.Links.Select(l => l.Url));
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void Truncated_in_the_middle_of_a_byte_keeps_earlier_links_and_says_so()
    {
        var data = Rsdf(UrlA, UrlB);
        var cut = data[..^1];

        var result = ContainerReader.Read(cut, "links.rsdf");

        Assert.Contains(result.Links, l => l.Url == UrlA);
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.Truncated);
    }

    [Fact]
    public void Truncated_inside_a_line_keeps_earlier_links_and_reports_the_cut_line()
    {
        var data = Rsdf(UrlA, UrlB);
        var cut = data[..(data.Length - 20)];

        var result = ContainerReader.Read(cut, "links.rsdf");

        Assert.Equal(UrlA, Assert.Single(result.Links).Url);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Wrong_base64_on_one_line_reports_it_then_recovers_after_one_block()
    {
        // Line 3 still decodes because CFB resynchronises, while line 2 loses its first block with the bad line.
        var lines = RsdfCipherLines(false, $"CCF: {UrlA}", $"CCF: {UrlB}", $"CCF: {UrlC}", $"CCF: {UrlA}");
        lines[1] = "this is @@ not base64";
        var result = ContainerReader.Read(ToHexFile(string.Join("\n", lines)), "links.rsdf");

        Assert.Equal(UrlA, result.Links[0].Url);
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.Malformed && p.Message.Contains("Entry 2", StringComparison.Ordinal));
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.InvalidEntry && p.Message.Contains("Entry 3", StringComparison.Ordinal));
        Assert.Equal(UrlA, result.Links[^1].Url);
    }

    [Theory]
    [InlineData("QUJD=")]
    [InlineData("QUJDRA")]
    [InlineData("QU=JD")]
    [InlineData("====")]
    public void Bad_padding_is_reported_and_does_not_throw(string line)
    {
        var result = ContainerReader.Read(ToHexFile(line), "links.rsdf");

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.Malformed);
    }

    [Fact]
    public void Plaintext_that_is_not_a_link_is_reported_without_being_quoted()
    {
        var result = ContainerReader.Read(RsdfWith("\n", false, "CCF: not a link at all"), "links.rsdf");

        Assert.False(result.Succeeded);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(ContainerProblemKind.InvalidEntry, problem.Kind);
        Assert.DoesNotContain("not a link at all", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Data_after_the_hex_is_ignored_and_reported()
    {
        var data = Rsdf(UrlA).Concat("<html>trailing</html>"u8.ToArray()).ToArray();

        var result = ContainerReader.Read(data, "links.rsdf");

        Assert.Equal(UrlA, Assert.Single(result.Links).Url);
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.Malformed);
    }

    [Fact]
    public void Named_rsdf_without_hex_says_so()
    {
        var result = ContainerReader.Read("zzzz not hex"u8.ToArray(), "links.rsdf");

        Assert.Equal(ContainerFormat.Rsdf, result.Format);
        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.Malformed);
    }

    [Fact]
    public void Link_limit_stops_reading_and_is_reported_once()
    {
        var links = Enumerable.Range(0, 5).Select(i => $"https://files.example.test/{i}").ToArray();

        var result = ContainerReader.Read(Rsdf(links), "links.rsdf", new ContainerLimits { MaxLinks = 2 });

        Assert.Equal(2, result.Links.Count);
        Assert.Equal(ContainerProblemKind.TooManyEntries, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Exactly_at_the_link_limit_is_not_a_problem()
    {
        var result = ContainerReader.Read(Rsdf(UrlA, UrlB), "links.rsdf", new ContainerLimits { MaxLinks = 2 });

        Assert.Equal(2, result.Links.Count);
        Assert.Empty(result.Problems);
    }

    [Fact]
    public void An_oversized_line_is_skipped_exactly_so_the_next_line_still_decodes()
    {
        var huge = "CCF: https://files.example.test/" + new string('a', 20_000);
        var result = ContainerReader.Read(RsdfWith("\n", false, $"CCF: {UrlA}", huge, $"CCF: {UrlB}"), "links.rsdf");

        Assert.Equal([UrlA, UrlB], result.Links.Select(l => l.Url));
        Assert.Equal(ContainerProblemKind.InvalidEntry, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Hex_of_random_bytes_gives_a_clear_failure()
    {
        var random = new byte[4096];
        new Random(7).NextBytes(random);

        var result = ContainerReader.Read(ToHexFile(random), "links.rsdf");

        Assert.False(result.Succeeded);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void Decoder_loop_matches_a_platform_cfb8_decrypt_of_the_same_bytes()
    {
        using var aes = Aes.Create();
        aes.Key = RsdfReader.Key;
        var iv = RsdfReader.InitialRegister();
        var plain = Encoding.UTF8.GetBytes($"CCF: {UrlC}");
        var cipher = aes.EncryptCfb(plain, iv, PaddingMode.None, 8);
        Assert.Equal(plain, aes.DecryptCfb(cipher, iv, PaddingMode.None, 8));

        var result = ContainerReader.Read(ToHexFile(Convert.ToBase64String(cipher)), "links.rsdf");

        Assert.Equal(UrlC, Assert.Single(result.Links).Url);
    }
}
