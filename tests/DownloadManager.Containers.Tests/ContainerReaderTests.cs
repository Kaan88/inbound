using System.Text;
using static DownloadManager.Containers.Tests.ContainerFixtures;

namespace DownloadManager.Containers.Tests;

public class ContainerReaderTests
{
    private static readonly string Xml = CryptLoadXml(PackageXml("P", Password, "note", DownloadXml(UrlA, "one.rar", "10"), DownloadXml(UrlB)));

    private static string WorkDirectory()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "test-work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("links.rsdf")]
    [InlineData("links.ccf")]
    [InlineData("links.dlc")]
    [InlineData("links.txt")]
    public void Empty_input_is_reported(string? name)
    {
        var result = ContainerReader.Read(Array.Empty<byte>(), name);

        Assert.False(result.Succeeded);
        Assert.Equal(ContainerProblemKind.Empty, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Null_bytes_are_treated_as_empty()
    {
        var result = ContainerReader.Read((byte[])null!, "links.ccf");
        Assert.Equal(ContainerProblemKind.Empty, Assert.Single(result.Problems).Kind);
    }

    [Theory]
    [InlineData("   \r\n\t ")]
    [InlineData("\uFEFF  ")]
    public void Blank_input_is_reported(string text)
    {
        var result = ContainerReader.Read(Encoding.UTF8.GetBytes(text), "links.rsdf");
        Assert.Equal(ContainerProblemKind.Empty, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Input_over_the_size_limit_is_refused_before_any_work()
    {
        var result = ContainerReader.Read(new byte[1025], "links.ccf", new ContainerLimits { MaxInputBytes = 1024 });

        Assert.Equal(ContainerFormat.Ccf, result.Format);
        Assert.Equal(ContainerProblemKind.TooLarge, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Huge_input_at_the_default_limit_is_refused()
    {
        var result = ContainerReader.Read(new byte[ContainerLimits.Default.MaxInputBytes + 1], "links.ccf");
        Assert.Equal(ContainerProblemKind.TooLarge, Assert.Single(result.Problems).Kind);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("links.ccf")]
    [InlineData("links.rsdf")]
    [InlineData("links.dlc")]
    public async Task Worst_case_input_just_inside_the_limit_finishes_promptly(string? name)
    {
        var size = (int)ContainerLimits.Default.MaxInputBytes;

        var cases = new List<byte[]>
        {
            Encoding.ASCII.GetBytes(new string('A', size)),
            Encoding.ASCII.GetBytes(string.Join('\n', Enumerable.Repeat("QUJDRA==", size / 9))),
            ToHexFile(Encoding.ASCII.GetBytes(string.Join('\n', Enumerable.Repeat("QUJDRA==", (size / 2) / 9)))),
            CcfFormatConstants.Ccf30Magic.ToArray().Concat(new byte[5 + (size - 11) / 64 * 64]).ToArray(),
            new byte[size / 16 * 16],
        };

        foreach (var data in cases)
        {
            var result = await Task.Run(() => ContainerReader.Read(data, name)).WaitAsync(TimeSpan.FromSeconds(60));
            Assert.NotEmpty(result.Problems);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("links.ccf")]
    [InlineData("links.rsdf")]
    [InlineData("links.dlc")]
    public void Html_error_page_is_reported_as_not_a_container(string? name)
    {
        var html = "<!doctype html><html><head><title>404 - Not Found</title></head><body>https://files.example.test/x</body></html>"u8;

        var result = ContainerReader.Read(html.ToArray(), name);

        Assert.Equal(ContainerFormat.Unknown, result.Format);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(ContainerProblemKind.NotAContainer, problem.Kind);
        Assert.DoesNotContain("example.test", problem.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""<?xml version="1.0"?><!DOCTYPE foo [<!ENTITY xxe SYSTEM "file:///c:/windows/win.ini">]><foo>&xxe;</foo>""")]
    [InlineData("""<!DOCTYPE lolz [<!ENTITY a "a"><!ENTITY b "&a;&a;&a;&a;&a;&a;&a;&a;">]><lolz>&b;</lolz>""")]
    [InlineData("""  <root attr="1"><url>https://files.example.test/x</url></root>""")]
    public void Xml_with_or_without_a_dtd_at_the_top_level_is_never_parsed(string xml)
    {
        var result = ContainerReader.Read(Encoding.UTF8.GetBytes(xml), "links.ccf");

        Assert.False(result.Succeeded);
        Assert.Equal(ContainerProblemKind.NotAContainer, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Plain_text_without_a_known_name_is_not_a_container()
    {
        var result = ContainerReader.Read("hello, this is just a text file"u8.ToArray(), "notes.txt");

        Assert.Equal(ContainerFormat.Unknown, result.Format);
        Assert.Equal(ContainerProblemKind.NotAContainer, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Binary_junk_without_a_known_name_is_not_a_container()
    {
        var junk = new byte[1600];
        new Random(11).NextBytes(junk);

        var result = ContainerReader.Read(junk, "data.bin");

        Assert.Equal(ContainerProblemKind.NotAContainer, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Zip_bytes_are_not_expanded()
    {
        // No code path decompresses, so a bomb is only ever its own small size. These are the bytes of an empty zip.
        byte[] zip = [0x50, 0x4B, 0x05, 0x06, .. new byte[18]];

        foreach (var name in new[] { "links.ccf", "links.rsdf", "links.dlc", "links.zip" })
        {
            var result = ContainerReader.Read(zip, name);
            Assert.False(result.Succeeded);
            Assert.NotEmpty(result.Problems);
        }
    }

    [Fact]
    public void Content_beats_a_wrong_or_missing_extension()
    {
        Assert.Equal(ContainerFormat.Rsdf, ContainerReader.Read(Rsdf(UrlA), "links.ccf").Format);
        Assert.Equal(ContainerFormat.Rsdf, ContainerReader.Read(Rsdf(UrlA), "links.txt").Format);
        Assert.Equal(ContainerFormat.Rsdf, ContainerReader.Read(Rsdf(UrlA)).Format);

        Assert.Equal(ContainerFormat.Ccf, ContainerReader.Read(Ccf30(Xml), "links.rsdf").Format);
        Assert.Equal(ContainerFormat.Ccf, ContainerReader.Read(CcfLegacy(Xml), "links.rsdf").Format);
        Assert.Equal(ContainerFormat.Ccf, ContainerReader.Read(Ccf50(Xml), "links.dlc").Format);
        Assert.Equal(ContainerFormat.Ccf, ContainerReader.Read(Ccf50(Xml), "LINKS.CCF").Format);

        Assert.Equal(ContainerFormat.Dlc, ContainerReader.Read(Encoding.ASCII.GetBytes(Dlc()), "links.txt").Format);
        Assert.Equal(ContainerFormat.Dlc, ContainerReader.Read(Encoding.ASCII.GetBytes(Dlc())).Format);
    }

    [Fact]
    public void Corrupted_and_truncated_fixtures_never_throw()
    {
        var seeds = new List<byte[]>
        {
            Rsdf(UrlA, UrlB, UrlC),
            CcfLegacy(Xml),
            Ccf50(Xml),
            Ccf30(Xml),
            Encoding.ASCII.GetBytes(Dlc()),
        };

        var random = new Random(42);
        var names = new string?[] { null, "a.ccf", "a.rsdf", "a.dlc", "a.bin" };

        foreach (var seed in seeds)
        {
            foreach (var name in names)
            {
                for (var length = 0; length < seed.Length; length += Math.Max(1, seed.Length / 37))
                {
                    Assert.NotNull(ContainerReader.Read(seed[..length], name));
                }

                for (var round = 0; round < 40; round++)
                {
                    var damaged = (byte[])seed.Clone();
                    for (var flip = 0; flip < 1 + (round % 8); flip++)
                    {
                        damaged[random.Next(damaged.Length)] = (byte)random.Next(256);
                    }

                    Assert.NotNull(ContainerReader.Read(damaged, name));
                }
            }
        }
    }

    [Fact]
    public void Problem_list_is_bounded_when_every_entry_is_bad()
    {
        var lines = string.Join('\n', Enumerable.Repeat("@@@", 1000));

        var result = ContainerReader.Read(ToHexFile(lines), "links.rsdf", new ContainerLimits { MaxProblems = 5 });

        Assert.Equal(6, result.Problems.Count);
        Assert.Equal(ContainerProblemKind.TooManyEntries, result.Problems[^1].Kind);
    }

    [Fact]
    public void Nothing_printable_quotes_a_url_or_a_password()
    {
        var result = ContainerReader.Read(CcfLegacy(Xml), "links.ccf");
        Assert.True(result.Succeeded);

        var printed = new List<string> { result.ToString() };
        printed.AddRange(result.Links.Select(l => l.ToString()));
        printed.AddRange(result.Links.Select(l => $"{l}"));
        printed.AddRange(result.Packages.Select(p => p.ToString()));
        printed.AddRange(result.Problems.Select(p => p.ToString()));

        Assert.All(printed, text =>
        {
            Assert.DoesNotContain("example.test", text, StringComparison.Ordinal);
            Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
            Assert.DoesNotContain("one.rar", text, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_file_on_disk_is_read_and_its_name_guides_detection()
    {
        var path = Path.Combine(WorkDirectory(), "links.rsdf");
        File.WriteAllBytes(path, Rsdf(UrlA));

        var result = ContainerReader.Read(path);

        Assert.Equal(UrlA, Assert.Single(result.Links).Url);
    }

    [Fact]
    public async Task ReadAsync_reads_a_file_and_honours_cancellation()
    {
        var path = Path.Combine(WorkDirectory(), "links.ccf");
        await File.WriteAllBytesAsync(path, CcfLegacy(Xml));

        var result = await ContainerReader.ReadAsync(path);
        Assert.Equal(2, result.Links.Count);

        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ContainerReader.ReadAsync(path, cancellationToken: cancelled.Token));
    }

    [Fact]
    public void A_file_over_the_limit_is_refused_by_its_length()
    {
        var path = Path.Combine(WorkDirectory(), "big.ccf");
        File.WriteAllBytes(path, new byte[2048]);

        var result = ContainerReader.Read(path, new ContainerLimits { MaxInputBytes = 1024 });

        Assert.Equal(ContainerProblemKind.TooLarge, Assert.Single(result.Problems).Kind);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("does-not-exist.ccf")]
    [InlineData("bad\0path.ccf")]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData(@"\\.\CON")]
    [InlineData(@"\\.\NUL")]
    [InlineData(@"\\.\pipe\does-not-exist")]
    public async Task Unusable_paths_are_reported_and_do_not_hang(string path)
    {
        var result = await Task.Run(() => ContainerReader.Read(path)).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.False(result.Succeeded);
        Assert.Equal(ContainerProblemKind.FileAccess, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void A_directory_is_reported_and_a_null_path_is_too()
    {
        Assert.Equal(ContainerProblemKind.FileAccess, Assert.Single(ContainerReader.Read(WorkDirectory()).Problems).Kind);
        Assert.Equal(ContainerProblemKind.FileAccess, Assert.Single(ContainerReader.Read((string)null!).Problems).Kind);
    }

    [Fact]
    public void A_locked_file_is_reported()
    {
        var path = Path.Combine(WorkDirectory(), "locked.ccf");
        File.WriteAllBytes(path, CcfLegacy(Xml));
        using var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = ContainerReader.Read(path);

        Assert.Equal(ContainerProblemKind.FileAccess, Assert.Single(result.Problems).Kind);
    }

    [Fact]
    public void Library_has_no_runtime_ui_or_ipc_dependency()
    {
        var references = typeof(ContainerReader).Assembly.GetReferencedAssemblies().Select(a => a.Name).ToArray();

        Assert.DoesNotContain(references, name => name!.Contains("Runtime.Host", StringComparison.Ordinal) || name.Contains("DownloadManager.Runtime", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name!.Contains("DownloadManager.Contracts", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name!.Contains("WinUI", StringComparison.Ordinal));
    }
}
