using DownloadManager.Cli;
using Xunit;

namespace DownloadManager.Integration.Tests;

/// <summary>UI.B37: which Host file the CLI looks for, and where, on each system.</summary>
public sealed class CliHostLocatorTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "inbound-app", "lib");

    [Fact]
    public void Linux_looks_for_inbound_host_beside_the_cli_and_one_folder_up()
    {
        var found = HostLocator.Candidates(linux: true, Base, fromEnv: null);
        Assert.Equal(Path.Combine(Base, "inbound-host"), found[0]);
        Assert.Equal(Path.Combine(Base, "..", "inbound-host"), found[1]);
        Assert.All(found, c => Assert.EndsWith("inbound-host", c));
        Assert.Equal("inbound-host", HostLocator.HostFileNameFor(linux: true));
    }

    [Fact]
    public void Windows_looks_for_the_exe_as_before()
    {
        var found = HostLocator.Candidates(linux: false, Base, fromEnv: null);
        Assert.Equal(4, found.Count);
        Assert.Equal(Path.Combine(Base, "DownloadManager.Host.exe"), found[0]);
        Assert.All(found, c => Assert.EndsWith("DownloadManager.Host.exe", c));
        Assert.Equal("DownloadManager.Host.exe", HostLocator.HostFileNameFor(linux: false));
    }

    [Fact]
    public void An_address_in_the_environment_is_tried_first_on_both()
    {
        Assert.Equal("/x/host", HostLocator.Candidates(true, Base, "/x/host")[0]);
        Assert.Equal("/x/host", HostLocator.Candidates(false, Base, "/x/host")[0]);
    }
}
