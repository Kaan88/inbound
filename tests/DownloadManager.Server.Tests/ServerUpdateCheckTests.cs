using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace DownloadManager.Server.Tests;

/// <summary>UPD.D1: the server's Docker update check reads a signed manifest and never installs.</summary>
public sealed class ServerUpdateCheckTests
{
    [Fact]
    public async Task AValidManifestRecordsTheVersionAndNotes()
    {
        var (publicKey, json) = SignedManifest("0.7.4", "ghcr.io/kaan88/inbound:latest",
            notes: [new ChangelogItem { Text = "A new web UI line.", Tags = [ChangelogTags.WebUi] }]);
        var service = NewService(out var paths, out var engine);
        using var check = new ServerUpdateCheck(service, _ => { }, publicKey, "0.7.2", "https://update.test/manifest-docker.json",
            new StubHandler(_ => Json(new StringContent(json))));
        try
        {
            await check.CheckAsync();
            Assert.Equal("0.7.4", service.LatestVersion);
            var notes = service.ServerUpdateNotes();
            Assert.Equal("0.7.4", notes.AvailableVersion);
            Assert.NotNull(notes.Notes);
            Assert.Contains(notes.Notes!, line => line.Text == "A new web UI line.");
        }
        finally
        {
            engine.Dispose();
            Delete(paths);
        }
    }

    [Fact]
    public async Task AnInvalidSignatureRecordsNothing()
    {
        var (publicKey, json) = SignedManifest("0.7.4", "ghcr.io/kaan88/inbound:latest", valid: false);
        var service = NewService(out var paths, out var engine);
        using var check = new ServerUpdateCheck(service, _ => { }, publicKey, "0.7.2", "https://update.test/manifest-docker.json",
            new StubHandler(_ => Json(new StringContent(json))));
        try
        {
            await check.CheckAsync();
            Assert.Null(service.LatestVersion);
            Assert.Null(service.ServerUpdateNotes().AvailableVersion);
        }
        finally
        {
            engine.Dispose();
            Delete(paths);
        }
    }

    [Fact]
    public async Task AFailedFetchRecordsNothingButDoesNotThrow()
    {
        var service = NewService(out var paths, out var engine);
        using var check = new ServerUpdateCheck(service, _ => { }, "public-key", "0.7.2", "https://update.test/manifest-docker.json",
            new StubHandler(_ => throw new HttpRequestException("no network")));
        try
        {
            await check.CheckAsync();
            Assert.Null(service.LatestVersion);
        }
        finally
        {
            engine.Dispose();
            Delete(paths);
        }
    }

    private static DownloadManagerService NewService(out StoragePaths paths, out HttpDownloadEngine engine)
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "test-work", "upd-check-" + Guid.NewGuid().ToString("N"));
        paths = new StoragePaths(Path.Combine(dir, "state"));
        engine = new HttpDownloadEngine(new EngineOptions { AllowLocalNetwork = true, MaxAttempts = 1 }, checkpoints: new JsonCheckpointStore(paths));
        return new DownloadManagerService(paths, new JsonJobStore(paths), new JsonCheckpointStore(paths), new JsonSettingsStore(paths), engine,
            enableSchedulerTimer: false);
    }

    private static void Delete(StoragePaths paths)
    {
        try { Directory.Delete(Path.GetDirectoryName(paths.Root)!, recursive: true); } catch (IOException) { }
    }

    private static (string PublicKey, string Json) SignedManifest(string version, string installerUrl,
        IReadOnlyList<ChangelogItem>? notes = null, bool valid = true)
    {
        using var rsa = RSA.Create(2048);
        var publicKey = rsa.ExportSubjectPublicKeyInfoPem();
        var canonical = $"{version}\nstable\n{installerUrl}\n0\n";
        var signature = valid
            ? Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
            : Convert.ToBase64String(new byte[256]);
        string? notesSignature = null;
        if (notes is not null)
        {
            var camel = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var text = UpdateManifestVerifier.NotesCanonical(canonical, JsonSerializer.SerializeToElement(notes, camel))!;
            notesSignature = Convert.ToBase64String(rsa.SignData(Encoding.UTF8.GetBytes(text), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }

        var manifest = new
        {
            version,
            channel = "stable",
            installerUrl,
            size = 0,
            sha256 = string.Empty,
            signature,
            notes,
            notesSignature,
        };
        var json = JsonSerializer.Serialize(manifest, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
        return (publicKey, json);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(HttpContent content)
        => new(System.Net.HttpStatusCode.OK) { Content = content };
}
