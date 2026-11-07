using System.Text;
using static DownloadManager.Containers.Tests.ContainerFixtures;

namespace DownloadManager.Containers.Tests;

public class DlcSupportTests
{
    [Fact]
    public void Describe_explains_why_it_is_not_supported()
    {
        var description = DlcSupport.Describe();

        Assert.False(description.CanOpen);
        Assert.False(description.IsRecognised);
        Assert.Contains("can't be opened", description.Explanation, StringComparison.Ordinal);
        Assert.Contains("JDownloader's online service", description.Explanation, StringComparison.Ordinal);
        Assert.Contains("Impersonating", description.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public void Describe_recognises_a_dlc_by_shape_without_opening_it()
    {
        var description = DlcSupport.Describe(Encoding.ASCII.GetBytes(Dlc(payloadBlocks: 4)));

        Assert.True(description.IsRecognised);
        Assert.False(description.CanOpen);
        Assert.Equal(64, description.PayloadBytes);
    }

    [Fact]
    public void Reader_returns_format_and_explanation_with_no_links()
    {
        var result = ContainerReader.Read(Encoding.ASCII.GetBytes(Dlc()), "links.dlc");

        Assert.Equal(ContainerFormat.Dlc, result.Format);
        Assert.False(result.Succeeded);
        Assert.Empty(result.Packages);
        var problem = Assert.Single(result.Problems);
        Assert.Equal(ContainerProblemKind.NotSupported, problem.Kind);
        Assert.Equal(DlcSupport.Explanation, problem.Message);
    }

    [Fact]
    public void Wrapped_lines_are_still_recognised()
    {
        var text = Dlc();
        var wrapped = text[..40] + "\r\n" + text[40..] + "\n";

        Assert.True(DlcSupport.Describe(Encoding.ASCII.GetBytes(wrapped)).IsRecognised);
    }

    [Theory]
    [InlineData("!!!! not base64 at all")]
    [InlineData("QUJD")]
    public void A_bad_file_named_dlc_gets_both_messages(string text)
    {
        var result = ContainerReader.Read(Encoding.ASCII.GetBytes(text), "links.dlc");

        Assert.Equal(ContainerFormat.Dlc, result.Format);
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.Malformed);
        Assert.Contains(result.Problems, p => p.Kind == ContainerProblemKind.NotSupported);
    }

    [Fact]
    public void Payload_that_is_not_whole_blocks_is_not_recognised()
    {
        var payload = Convert.ToBase64String(new byte[33]);
        var tail = Convert.ToBase64String(new byte[64]);

        Assert.False(DlcSupport.Describe(Encoding.ASCII.GetBytes(payload + tail)).IsRecognised);
    }

    [Fact]
    public void Tail_of_the_wrong_size_or_with_bad_padding_is_not_recognised()
    {
        var payload = Convert.ToBase64String(new byte[32]);
        var goodTail = Convert.ToBase64String(new byte[64]);

        Assert.False(DlcSupport.Describe(Encoding.ASCII.GetBytes(payload + goodTail[..^4])).IsRecognised);
        Assert.False(DlcSupport.Describe(Encoding.ASCII.GetBytes(payload + "=" + goodTail[1..])).IsRecognised);
    }

    [Fact]
    public void Base64_with_the_tail_alone_is_not_recognised()
    {
        Assert.False(DlcSupport.Describe(Encoding.ASCII.GetBytes(Convert.ToBase64String(new byte[64]))).IsRecognised);
    }

    [Fact]
    public void Oversized_input_is_not_examined()
    {
        var description = DlcSupport.Describe(new byte[ContainerLimits.Default.MaxInputBytes + 1]);
        Assert.False(description.IsRecognised);
    }

    [Fact]
    public void Dlc_code_holds_no_network_type()
    {
        var fields = typeof(DlcSupport).GetMembers(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        Assert.DoesNotContain(fields, m => m is System.Reflection.FieldInfo f && typeof(HttpClient).IsAssignableFrom(f.FieldType));
    }
}
