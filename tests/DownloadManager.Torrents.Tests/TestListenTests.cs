namespace DownloadManager.Torrents.Tests;

/// <summary>The walk-through variable: a port keeps the worker on 127.0.0.1 with DHT off; anything else changes nothing.</summary>
[Collection("TestListenVariable")]
public sealed class TestListenTests
{
    [Fact]
    public void APortKeepsTheWorkerOnLoopbackWithoutDht()
    {
        var before = Environment.GetEnvironmentVariable(TorrentSetup.TestListenVariable);
        try
        {
            Environment.SetEnvironmentVariable(TorrentSetup.TestListenVariable, "6899");
            var listen = TorrentSetup.TestListenOptions()!.Listen!();
            Assert.Equal(6899, listen["port"]!.GetValue<int>());
            Assert.Equal("127.0.0.1", listen["interfaces"]![0]!.GetValue<string>());
            Assert.False(listen["dht"]!.GetValue<bool>());

            foreach (var value in new[] { "", "0", "70000", "x", "-1" })
            {
                Environment.SetEnvironmentVariable(TorrentSetup.TestListenVariable, value);
                Assert.Null(TorrentSetup.TestListenOptions());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(TorrentSetup.TestListenVariable, before);
        }
    }
}
