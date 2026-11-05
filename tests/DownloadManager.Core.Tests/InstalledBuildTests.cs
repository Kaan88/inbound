using System.Text.Json;
using DownloadManager.Core;

namespace DownloadManager.Core.Tests;

public sealed class InstalledBuildTests
{
    [Fact]
    public void MetadataAheadOfInstalledBinaryDoesNotHideAnUpdate()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "dm-version-" + Guid.NewGuid());
        Directory.CreateDirectory(Path.Combine(root, "app"));
        try
        {
            File.Copy(typeof(InstalledBuild).Assembly.Location,
                Path.Combine(root, "app", "DownloadManager.Host.dll"));
            File.WriteAllText(Path.Combine(root, "install.json"), JsonSerializer.Serialize(new
            {
                version = "999.0.0", channel = "stable",
            }));
            var installed = InstalledBuild.TryLoad(root);
            Assert.NotNull(installed);
            Assert.Equal(typeof(InstalledBuild).Assembly.GetName().Version!.ToString(3), installed.Version);
        }
        finally { Directory.Delete(root, true); }
    }
}
