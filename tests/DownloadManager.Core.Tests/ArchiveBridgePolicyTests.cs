using System.Reflection;
using DownloadManager.Contracts;

namespace DownloadManager.Core.Tests;

public sealed class ArchiveBridgePolicyTests
{
    private static IEnumerable<string> AllArchiveMessageTypes() => typeof(ArchiveMessageTypes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.IsLiteral && field.Name != nameof(ArchiveMessageTypes.Prefix))
        .Select(field => (string)field.GetRawConstantValue()!);

    [Fact]
    public void TheBrowserBridgeCarriesNoArchiveMessageEitherWay()
    {
        var types = AllArchiveMessageTypes().ToList();

        Assert.Contains("archive.setPassword", types);
        Assert.Contains("archive.settings.set", types);
        Assert.Contains("archive.changed", types);
        Assert.All(types, type =>
        {
            Assert.False(NativeBridgePolicy.AllowFromExtension(type), $"{type} reached the Host from the browser.");
            Assert.False(NativeBridgePolicy.AllowToExtension(type), $"{type} reached the browser from the Host.");
            Assert.Null(NativeBridgePolicy.ToExtension(IpcMessage.Create(type)));
        });
    }
}
