using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Android.Content;
using DownloadManager.Contracts;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Runtime;

namespace AndroidTrial;

/// <summary>
/// mode=probe: the first-use cost of the pieces behind the host start, each timed once in a fresh process (cold) and, where it
/// makes sense, a second time (warm), in a fixed order, on the state folder an earlier mode=host run left. Reads the settings file
/// and one job file; changes nothing. Result: probe-result-&lt;runId&gt;.json in the app's external files folder.
/// </summary>
internal static class JsonProbe
{
    public static async Task RunAsync(Context context, string runId, Action<string> say)
    {
        var json = new JsonObject { ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription };
        var steps = new JsonArray();
        double T(string name, Action body)
        {
            var sw = Stopwatch.StartNew();
            var jit0 = Jit();
            string? err = null;
            try { body(); } catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
            sw.Stop();
            steps.Add(new JsonObject { ["step"] = name, ["ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1), ["methodsCompiled"] = Jit() - jit0, ["error"] = err });
            return sw.Elapsed.TotalMilliseconds;
        }

        var root = HostScenario.StateRoot(context);
        var settingsText = File.ReadAllText(Path.Combine(root, "settings.json"));
        var jobFile = Directory.EnumerateFiles(Path.Combine(root, "jobs"), "*.json").FirstOrDefault();
        var jobText = jobFile is null ? null : File.ReadAllText(jobFile);
        json["settingsBytes"] = settingsText.Length;
        json["jobBytes"] = jobText?.Length;

        // --- the secret protector (220 ms in the host start) ---
        var key = new byte[32];
        T("RandomNumberGenerator.GetBytes(32) first use", () => RandomNumberGenerator.GetBytes(32).CopyTo(key, 0));
        T("RandomNumberGenerator.GetBytes(32) second", () => RandomNumberGenerator.GetBytes(32));
        T("new AesGcm(key,16) + one Encrypt, first use", () =>
        {
            using var aes = new AesGcm(key, 16);
            var nonce = new byte[12]; var ct = new byte[16]; var tag = new byte[16];
            aes.Encrypt(nonce, new byte[16], ct, tag);
        });
        T("new AesGcm(key,16) + one Encrypt, second", () =>
        {
            using var aes = new AesGcm(key, 16);
            var nonce = new byte[12]; var ct = new byte[16]; var tag = new byte[16];
            aes.Encrypt(nonce, new byte[16], ct, tag);
        });
        var keyDir = Path.Combine(root, "key");
        T("AesGcmSecretProtector.OpenForDataFolder (after the above)", () => SecretProtector.Use(AesGcmSecretProtector.OpenForDataFolder(
            Path.Combine(keyDir, AesGcmSecretProtector.DefaultFileName), keyDir, discardUnreadable: false)));

        // --- JSON: PersistenceJson.Options (internal; read by reflection), AppSettings, DownloadJob ---
        JsonSerializerOptions? options = null;
        T("PersistenceJson.Options (static init: options + source-gen context + reflection resolver)", () =>
            options = (JsonSerializerOptions?)typeof(JsonJobStore).Assembly.GetType("DownloadManager.Persistence.PersistenceJson")!
                .GetProperty("Options", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
                ?? (JsonSerializerOptions?)typeof(JsonJobStore).Assembly.GetType("DownloadManager.Persistence.PersistenceJson")!
                    .GetField("Options", BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null));
        T("Deserialize<AppSettings> #1 (cold: builds the type info)", () => JsonSerializer.Deserialize<AppSettings>(settingsText, options!));
        T("Deserialize<AppSettings> #2", () => JsonSerializer.Deserialize<AppSettings>(settingsText, options!));
        T("Deserialize<AppSettings> #3", () => JsonSerializer.Deserialize<AppSettings>(settingsText, options!));
        if (jobText is not null)
        {
            T("Deserialize<DownloadJob> #1 (cold)", () => JsonSerializer.Deserialize<DownloadJob>(jobText, options!));
            T("Deserialize<DownloadJob> #2", () => JsonSerializer.Deserialize<DownloadJob>(jobText, options!));
        }

        var paths = new StoragePaths(root);
        T("JsonSettingsStore.LoadAsync (after the JSON above is warm)", () => new JsonSettingsStore(paths).LoadAsync().GetAwaiter().GetResult());
        T("JsonSettingsStore.LoadAsync again", () => new JsonSettingsStore(paths).LoadAsync().GetAwaiter().GetResult());

        // --- Contracts' reflection JSON (the hello and the snapshot) ---
        T("IpcMessage.Create(new HostHelloDto) #1 (cold)", () => IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { HostVersion = "0.8.0", HostKind = HostKinds.Server }));
        T("IpcMessage.Create(new HostHelloDto) #2", () => IpcMessage.Create(MessageTypes.ServerHello, new HostHelloDto { HostVersion = "0.8.0", HostKind = HostKinds.Server }));
        SnapshotDto? snap = null;
        T("build an empty SnapshotDto with default SettingsDto + IpcMessage.Create #1 (cold)", () =>
        {
            snap = new SnapshotDto { Jobs = [], Settings = new SettingsDto(), Status = new HostStatusDto() };
            IpcMessage.Create(MessageTypes.JobsSnapshot, snap);
        });
        T("IpcMessage.Create(snapshot) #2", () => IpcMessage.Create(MessageTypes.JobsSnapshot, snap!));

        json["steps"] = steps;
        var file = Path.Combine(HostScenario.ExtDir(context), $"probe-result-{runId}.json");
        File.WriteAllText(file, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        say("probe done " + file);
        await Task.CompletedTask;
    }

    private static long Jit()
    {
        try { return System.Runtime.JitInfo.GetCompiledMethodCount(); } catch (Exception) { return -1; }
    }
}
