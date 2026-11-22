using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DownloadManager.Core;
using DownloadManager.Persistence;
using DownloadManager.Tests.Shared;

namespace DownloadManager.Engine.Tests;

/// <summary>
/// PERF.10 (first half): the generated JSON metadata for the stored records must not change one byte of any file, and
/// every file an older build wrote must still load. "Old" below is <c>PersistenceJson.Options</c> exactly as it was built
/// before the context existed: pure reflection.
/// </summary>
public sealed class PersistenceJsonContextTests : IDisposable
{
    private readonly string _root = Path.Combine(AppContext.BaseDirectory, "test-work", "persistjson-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static JsonSerializerOptions OldOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>The generated metadata and nothing else (no reflection fallback), with the options' enum converter.</summary>
    private static JsonSerializerOptions GeneratedOnly()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            TypeInfoResolver = PersistenceJsonContext.Default,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static Type[] Registered() => CustomAttributeData.GetCustomAttributes(typeof(PersistenceJsonContext))
        .Where(a => a.AttributeType == typeof(JsonSerializableAttribute))
        .Select(a => (Type)a.ConstructorArguments[0].Value!).ToArray();

    /// <summary>Every type a store passes to <c>PersistenceJson.Options</c> (read from the stores' source types).</summary>
    private static readonly Type[] StoredByTheStores =
    [
        typeof(DownloadJob), typeof(DownloadCheckpoint), typeof(AppSettings), typeof(JsonJobStore.RequestClaim),
        typeof(DownloadManager.Core.Providers.RemoteJobRecord), typeof(DownloadManager.Core.Providers.RemoteAccountPacing),
        typeof(StoredRemoteAccount), typeof(List<TorrentFile>), typeof(DownloadManager.Core.Archives.ArchiveSettings),
        typeof(DownloadManager.Core.Archives.ArchiveSetRecord), typeof(DownloadManager.Core.Archives.ArchivePackagePassword),
        typeof(DownloadManager.Core.Archives.ArchiveSavedPasswords),
    ];

    [Fact]
    public void Every_type_a_store_writes_is_in_the_generated_context()
    {
        var registered = Registered().ToHashSet();
        var missing = StoredByTheStores.Where(t => !registered.Contains(t)).Select(t => t.Name).ToList();
        Assert.True(missing.Count == 0, "Add [JsonSerializable] to PersistenceJsonContext for: " + string.Join(", ", missing));
        // And the other way: nothing registered that no store uses (a leftover line would hide a rename).
        Assert.True(registered.All(t => StoredByTheStores.Contains(t)), "PersistenceJsonContext registers a type that is not in StoredByTheStores.");
    }

    [Fact]
    public void Every_stored_record_is_a_known_stored_type_in_the_source()
    {
        // Mechanical guard for the list above: every PersistenceJson.Options call site in the stores names one of those types.
        var source = Path.Combine(FindRepoRoot(), "src", "DownloadManager.Persistence");
        var text = string.Join("\n", Directory.GetFiles(source, "*.cs").Select(File.ReadAllText));
        foreach (var name in new[] { "DownloadJob", "RequestClaim", "DownloadCheckpoint", "AppSettings", "RemoteJobRecord", "RemoteAccountPacing",
                     "StoredRemoteAccount", "List<TorrentFile>" })
        {
            Assert.Contains($"Deserialize<{name}>(", text);
        }

        // JsonArchiveStore's generic ReadAsync<T> / WriteAsync<T>: its three types.
        foreach (var name in new[] { "ArchiveSettings", "ArchiveSetRecord", "ArchivePackagePassword" })
        {
            Assert.Contains($"ReadAsync<{name}>(", text);
        }

        // No other call site: the count of PersistenceJson.Options uses is fixed, so a new one makes this fail until it is reviewed.
        var uses = System.Text.RegularExpressions.Regex.Matches(text, @"PersistenceJson\.Options").Count;
        // ReadOnlyState also reads DownloadJob and AppSettings through their generated type metadata.
        Assert.Equal(23, uses);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "DownloadManager.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("The repository root was not found.");
    }

    [Fact]
    public void Generated_metadata_alone_gives_the_reflection_bytes_and_reads_them_back_for_every_stored_type()
    {
        var old = OldOptions();
        var generated = GeneratedOnly();
        var problems = new List<string>();
        foreach (var type in Registered())
        {
            for (var variant = 0; variant < 3; variant++)
            {
                var sample = JsonSampleFiller.Create(type, variant);
                if (sample is null) { problems.Add($"{type.Name}: no sample"); continue; }
                var expected = JsonSerializer.SerializeToUtf8Bytes(sample, type, old);
                var actual = JsonSerializer.SerializeToUtf8Bytes(sample, type, generated);
                if (!expected.AsSpan().SequenceEqual(actual)) { problems.Add($"{type.Name}: bytes differ"); continue; }

                var readNew = JsonSerializer.Deserialize(expected, type, generated);
                if (!expected.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(readNew, type, old))) problems.Add($"{type.Name}: generated read changed data");
                var readOld = JsonSerializer.Deserialize(expected, type, old);
                if (!expected.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(readOld, type, generated))) problems.Add($"{type.Name}: generated write of an old read differs");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_shared_options_object_gives_the_reflection_bytes_for_every_stored_type()
    {
        var old = OldOptions();
        var shared = (JsonSerializerOptions)typeof(JsonJobStore).Assembly.GetType("DownloadManager.Persistence.PersistenceJson")!
            .GetField("Options")!.GetValue(null)!;
        foreach (var type in Registered())
        {
            var sample = JsonSampleFiller.Create(type, 5)!;
            Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(sample, type, old), JsonSerializer.SerializeToUtf8Bytes(sample, type, shared));
        }

        Assert.NotNull(PersistenceJsonContext.Default.GetTypeInfo(typeof(DownloadJob)));
    }

    [Fact]
    public void Enums_are_camel_case_strings_read_in_any_case_and_from_numbers_as_before()
    {
        var old = OldOptions();
        var generated = GeneratedOnly();
        var tested = 0;
        foreach (var enumType in typeof(DownloadJob).Assembly.GetExportedTypes().Where(t => t.IsEnum))
        {
            // Only enums a stored record reaches have metadata (the others are never written on their own).
            try { generated.GetTypeInfo(enumType); } catch (NotSupportedException) { continue; }
            tested++;
            foreach (var value in Enum.GetValues(enumType))
            {
                var json = JsonSerializer.Serialize(value, enumType, old);
                Assert.Equal(json, JsonSerializer.Serialize(value, enumType, generated));
                var name = Enum.GetName(enumType, value)!;
                foreach (var text in new[] { json, "\"" + name + "\"", "\"" + name.ToUpperInvariant() + "\"", Convert.ToInt64(value).ToString() })
                {
                    var a = JsonSerializer.Deserialize(text, enumType, old);
                    var b = JsonSerializer.Deserialize(text, enumType, generated);
                    Assert.Equal(a, b);
                }
            }
        }

        Assert.True(tested > 0);
        // The spelling in a real file: a state is "paused", not "Paused" and not 2.
        Assert.Contains("\"state\": \"paused\"", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(
            new DownloadJob { State = DownloadState.Paused }, GeneratedOnly())));
    }

    [Fact]
    public async Task Old_files_still_load_through_the_stores_the_same_as_through_reflection()
    {
        var paths = new StoragePaths(_root);
        paths.EnsureCreated();
        var old = OldOptions();

        // A job file as an older build wrote it: member names in other case, numbers for enums, unknown members, members missing.
        var id = Guid.NewGuid();
        var legacyJob = $$"""
            {
              "SchemaVersion": 1,
              "Id": "{{id}}",
              "Url": "https://example.test/old.bin",
              "FileName": "old.bin",
              "State": 2,
              "DESTINATIONDIRECTORY": "/tmp/dl",
              "SomethingFromAnOlderBuild": { "x": [1, 2, 3] },
              "RequestHeaders": { "Accept": "*/*" },
              "RequestedConnections": 4
            }
            """;
        await File.WriteAllTextAsync(Path.Combine(paths.JobsDirectory, $"{id:N}.json"), legacyJob);
        var viaStore = Assert.Single(await new JsonJobStore(paths).LoadAllAsync());
        var viaOld = JsonSerializer.Deserialize<DownloadJob>(legacyJob, old)!;
        Assert.Equal(id, viaStore.Id);
        Assert.Equal(viaOld.State, viaStore.State);
        Assert.Equal(viaOld.DestinationDirectory, viaStore.DestinationDirectory);
        Assert.Equal(4, viaStore.RequestedConnections);
        Assert.Equal("*/*", viaStore.RequestHeaders["Accept"]);

        // Settings: a sparse file from an old install keeps every default the class gives.
        var legacySettings = """{ "schemaVersion": 1, "maxConcurrentDownloads": 5, "ExcludedHosts": ["a.test"], "unknown": true }""";
        await File.WriteAllTextAsync(paths.SettingsFile, legacySettings);
        var settings = await new JsonSettingsStore(paths).LoadAsync();
        var settingsOld = JsonSerializer.Deserialize<AppSettings>(legacySettings, old)!;
        Assert.Equal(settingsOld.MaxConcurrentDownloads, settings.MaxConcurrentDownloads);
        Assert.Equal(settingsOld.ExcludedHosts, settings.ExcludedHosts);
        Assert.Equal(settingsOld.DefaultQueueId, settings.DefaultQueueId);
        Assert.Equal(settingsOld.Queues.Count, settings.Queues.Count);

        // A file the new build writes reads back the same through the old options (a downgrade).
        var saved = await File.ReadAllTextAsync(paths.SettingsFile);
        var back = JsonSerializer.Deserialize<AppSettings>(saved, old)!;
        Assert.Equal(5, back.MaxConcurrentDownloads);

        // A checkpoint.
        var checkpointStore = new JsonCheckpointStore(paths);
        var checkpoint = JsonSampleFiller.Create<DownloadCheckpoint>(1);
        checkpoint.JobId = id;
        await checkpointStore.SaveAsync(checkpoint);
        var file = Directory.GetFiles(paths.CheckpointsDirectory, "*.json").Single();
        var onDisk = await File.ReadAllBytesAsync(file);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<DownloadCheckpoint>(onDisk, old), old), onDisk);
    }

    [Fact]
    public void Records_with_constructor_parameters_and_missing_members_read_the_same()
    {
        var old = OldOptions();
        var generated = GeneratedOnly();
        // TorrentFile(int Index, string Path, long Size, bool Wanted) is a positional record.
        foreach (var json in new[]
                 {
                     "[{\"index\":1,\"path\":\"a/b\",\"size\":9,\"wanted\":true}]",
                     "[{\"INDEX\":2,\"PATH\":\"c\"}]",
                     "[{}]",
                     "[{\"path\":null,\"unknown\":[1],\"size\":5}]",
                 })
        {
            string? errorOld = null, errorNew = null;
            List<TorrentFile>? a = null, b = null;
            try { a = JsonSerializer.Deserialize<List<TorrentFile>>(json, old); } catch (JsonException e) { errorOld = e.GetType().Name; }
            try { b = JsonSerializer.Deserialize<List<TorrentFile>>(json, generated); } catch (JsonException e) { errorNew = e.GetType().Name; }
            Assert.Equal(errorOld, errorNew);
            Assert.Equal(a, b);
        }

        // A sparse record read from "{}" keeps the same defaults.
        foreach (var type in Registered().Where(t => !t.IsGenericType))
        {
            object? a, b;
            string? errorOld = null, errorNew = null;
            try { a = JsonSerializer.Deserialize("{}", type, old); } catch (JsonException e) { a = null; errorOld = e.GetType().Name; }
            try { b = JsonSerializer.Deserialize("{}", type, generated); } catch (JsonException e) { b = null; errorNew = e.GetType().Name; }
            Assert.Equal(errorOld, errorNew);
            Assert.True(NoGuids(JsonSerializer.Serialize(a, type, old)) == NoGuids(JsonSerializer.Serialize(b, type, old)), $"{type.Name}: {NoGuids(JsonSerializer.Serialize(a, type, old))} vs {NoGuids(JsonSerializer.Serialize(b, type, old))}");
        }
    }

    // Some records make a fresh Guid (or time) when read from "{}"; everything else must match.
    private static string NoGuids(string json) => System.Text.RegularExpressions.Regex.Replace(
        System.Text.RegularExpressions.Regex.Replace(json, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", "GUID"),
        @"\d{4}-\d\d-\d\dT[\d:.]+[+-]\d\d:\d\d", "TIME");


    /// <summary>
    /// Difference found between reflection and generated metadata: for a type with init-only members the generated code does not
    /// run the property initializers when a member is missing from the file (it reads 0 or null, reflection keeps the default).
    /// So a stored type may not have a non-required init-only property (use set), except where there is no initializer.
    /// </summary>
    [Fact]
    public void Stored_types_have_no_init_only_members_with_defaults()
    {
        var offenders = new List<string>();
        var seen = new HashSet<Type>();
        void Walk(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (!seen.Add(type)) return;
            if (type.Namespace is null || !type.Namespace.StartsWith("DownloadManager", StringComparison.Ordinal)) { foreach (var a in type.IsGenericType ? type.GetGenericArguments() : []) Walk(a); return; }
            if (type.IsEnum) return;
            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var isInit = property.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true;
                var required = property.GetCustomAttributes().Any(a => a.GetType().Name == "RequiredMemberAttribute");
                var positional = type.GetConstructors().Any(c => c.GetParameters().Any(x => string.Equals(x.Name, property.Name, StringComparison.OrdinalIgnoreCase)));
                if (isInit && !required && !positional && !(type == typeof(TorrentFile) && property.Name == "Level")) offenders.Add($"{type.Name}.{property.Name}");
                Walk(property.PropertyType);
            }
        }

        foreach (var type in Registered()) Walk(type);
        Assert.True(offenders.Count == 0, "Use { get; set; } for: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Null_members_are_omitted_and_dates_guids_and_big_numbers_keep_their_text()
    {
        var job = new DownloadJob
        {
            Url = "https://example.test/x", TotalBytes = 9_007_199_254_740_993L, CreatedAt = new DateTimeOffset(2026, 11, 14, 1, 2, 3, 456, TimeSpan.FromHours(-5)),
            State = DownloadState.Downloading,
        };
        var text = Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(job, GeneratedOnly()));
        Assert.Equal(Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(job, OldOptions())), text);
        Assert.Contains("9007199254740993", text);
        Assert.Contains("2026-11-14T01:02:03.456-05:00", text);
        Assert.DoesNotContain("null", text);
    }
}
