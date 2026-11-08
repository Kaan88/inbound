using System.Text.Json;

namespace DownloadManager.Integration.Tests;

/// <summary>
/// PERF.3: the Host runs with GCConserveMemory 5, kept because it lowered the Host's memory by about 40% with
/// 5,000 jobs at the same CPU (PROGRESS.md, 2026-11-08). The setting lives in the Host's runtimeconfig.json,
/// which Setup's self-contained build carries too, so a project edit can't drop it unnoticed.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class HostGcSettingsTests
{
    [Fact]
    public void TheBuiltHostAsksTheGcToConserveMemory()
    {
        var config = Path.ChangeExtension(SandboxHost.HostExecutable, ".runtimeconfig.json");
        using var document = JsonDocument.Parse(File.ReadAllText(config));
        var properties = document.RootElement.GetProperty("runtimeOptions").GetProperty("configProperties");

        Assert.Equal(5, properties.GetProperty("System.GC.ConserveMemory").GetInt32());
        // Workstation GC: server GC gave the Host 50 threads for no gain in the same measurement.
        Assert.False(properties.TryGetProperty("System.GC.Server", out var server) && server.GetBoolean());
    }
}
