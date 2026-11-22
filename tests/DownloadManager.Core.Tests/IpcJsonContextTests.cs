using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using DownloadManager.Contracts;
using DownloadManager.Tests.Shared;

namespace DownloadManager.Core.Tests;

/// <summary>
/// PERF.10 (first half): the generated JSON metadata for the IPC contracts must not change one byte of the wire format.
/// "Old" below is the options object exactly as <c>IpcJson.Create()</c> built it before the context existed: pure reflection.
/// </summary>
public sealed class IpcJsonContextTests
{
    private static JsonSerializerOptions OldOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    /// <summary>Public types of Contracts that are never JSON on their own: helpers, clients, exceptions, view models.</summary>
    private static readonly HashSet<string> NotSerialised =
    [
        "IpcClient",                      // connection class
        "WebSocketIpcClient",             // connection class
        "ReconnectBackoff",               // timing helper
        "ServerTarget",                   // where a window connects; kept in the window's own store, not IPC
        "NativeHostKeyState",             // enum of the registration check, never sent
        "BridgeRole",                     // enum of NativeBridgePolicy, never sent
        "ChangelogSurface",               // enum of the changelog view helpers
        "ChangelogView", "ChangelogShownVersion", "ChangelogShownSection", "ChangelogElsewhere", "ChangelogFeatures", // window-side views
        "DownloadGroupSummary",           // computed for display
        "NativeHostRegistrationState",    // registry check result
        "JobListEntry", "TorrentListEntry", // saved-list file: JobListJsonContext (web defaults), not IpcJson
        "PlainHttpNotConfirmedException", "RemotePairingEndedException", "RemoteServerException",
        "ServerCertificateInfo",          // a certificate shown at pairing (ANDROID.7), never sent over IPC
        "SiteAddress",                    // SITES.11: the typed address read on the Host; the address travels as a string
    ];

    private static IEnumerable<Type> ContractTypes() => typeof(IpcMessage).Assembly.GetExportedTypes()
        .Where(t => !(t.IsAbstract && t.IsSealed)            // static classes
                    && !t.IsInterface && !t.IsAbstract
                    && !typeof(Exception).IsAssignableFrom(t)
                    && !typeof(Delegate).IsAssignableFrom(t)
                    && !t.IsDefined(typeof(System.Runtime.CompilerServices.CompilerGeneratedAttribute), false)
                    && t.Namespace == "DownloadManager.Contracts"
                    && t != typeof(IpcJsonContext)
                    && !typeof(JsonSerializerContext).IsAssignableFrom(t)
                    && !NotSerialised.Contains(t.Name));

    private static Type[] Registered() => CustomAttributeData.GetCustomAttributes(typeof(IpcJsonContext))
        .Where(a => a.AttributeType == typeof(JsonSerializableAttribute))
        .Select(a => (Type)a.ConstructorArguments[0].Value!).ToArray();

    [Fact]
    public void Every_public_contract_type_is_in_the_generated_context()
    {
        var missing = ContractTypes().Where(t => IpcJsonContext.Default.GetTypeInfo(t) is null).Select(t => t.FullName).ToList();
        Assert.True(missing.Count == 0,
            "Add [JsonSerializable(typeof(X))] to IpcJsonContext (or list the type in NotSerialised with a reason) for: " + string.Join(", ", missing));
    }

    [Fact]
    public void The_allow_list_names_real_types_only()
    {
        var names = typeof(IpcMessage).Assembly.GetExportedTypes().Select(t => t.Name).ToHashSet();
        Assert.All(NotSerialised, name => Assert.Contains(name, names));
    }

    [Fact]
    public void Every_registered_type_and_everything_it_reaches_resolves_without_reflection()
    {
        var pure = IpcJsonContext.Default.Options;
        var failures = new List<string>();
        var seen = new HashSet<Type>();
        foreach (var type in Registered()) Walk(type, pure, seen, failures);
        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    private static void Walk(Type type, JsonSerializerOptions options, HashSet<Type> seen, List<string> failures)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (!seen.Add(type)) return;
        if (type.IsEnum)
        {
            // The context names no enum converter; the first enum that reaches it needs one (see IpcJsonContext).
            failures.Add($"enum {type.Name} is reachable from a registered type");
            return;
        }

        if (typeof(System.Text.Json.Nodes.JsonNode).IsAssignableFrom(type))
        {
            // A JSON node is written by its own converter; its properties (Options, Parent) are never serialised.
            if (options.GetTypeInfo(type) is null) failures.Add(type.FullName ?? type.Name);
            return;
        }

        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type == typeof(DateTimeOffset) ||
            type == typeof(TimeSpan) || type == typeof(DateOnly) || type == typeof(TimeOnly) || type == typeof(Guid) || type == typeof(JsonElement) || type == typeof(DateTime) || type == typeof(object))
        {
            return;
        }

        if (options.GetTypeInfo(type) is null) { failures.Add(type.FullName ?? type.Name); return; }
        if (type.IsArray) { Walk(type.GetElementType()!, options, seen, failures); return; }
        if (type.IsGenericType)
        {
            foreach (var argument in type.GetGenericArguments()) Walk(argument, options, seen, failures);
            return;
        }

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length == 0 && property.GetCustomAttribute<JsonIgnoreAttribute>() is not { Condition: JsonIgnoreCondition.Always })
                Walk(property.PropertyType, options, seen, failures);
        }
    }

    [Fact]
    public void Generated_context_alone_gives_the_reflection_bytes_and_reads_them_back_for_every_registered_type()
    {
        var old = OldOptions();
        var pure = IpcJsonContext.Default.Options;
        var problems = new List<string>();
        foreach (var type in Registered())
        {
            for (var variant = 0; variant < 2; variant++)
            {
                var sample = JsonSampleFiller.Create(type, variant);
                if (sample is null) { problems.Add($"{type.Name}: no sample"); continue; }
                var expected = JsonSerializer.SerializeToUtf8Bytes(sample, type, old);
                var generated = JsonSerializer.SerializeToUtf8Bytes(sample, type, pure);
                if (!expected.AsSpan().SequenceEqual(generated))
                {
                    problems.Add($"{type.Name}: bytes differ{Environment.NewLine}{Encoding.UTF8.GetString(expected)}{Environment.NewLine}{Encoding.UTF8.GetString(generated)}");
                    continue;
                }

                // Old bytes read by the generated metadata, written again by the old: identical (nothing lost or invented).
                var read = JsonSerializer.Deserialize(expected, type, pure);
                var again = JsonSerializer.SerializeToUtf8Bytes(read, type, old);
                if (!expected.AsSpan().SequenceEqual(again)) problems.Add($"{type.Name}: generated read lost data: {Diff(expected, again)}");
                // And the other way round.
                var readOld = JsonSerializer.Deserialize(generated, type, old);
                var viaGenerated = JsonSerializer.SerializeToUtf8Bytes(readOld, type, pure);
                if (!expected.AsSpan().SequenceEqual(viaGenerated)) problems.Add($"{type.Name}: old read, generated write differs: {Diff(expected, viaGenerated)}");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    private static string Diff(byte[] a, byte[] b)
    {
        var i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        string Around(byte[] x) => Encoding.UTF8.GetString(x, Math.Max(0, i - 60), Math.Min(140, x.Length - Math.Max(0, i - 60)));
        return $"at {i}: expected ...{Around(a)}... got ...{Around(b)}...";
    }

    [Fact]
    public void Shared_options_object_gives_the_same_bytes_as_pure_reflection_for_every_registered_type()
    {
        var old = OldOptions();
        var problems = new List<string>();
        foreach (var type in Registered())
        {
            var sample = JsonSampleFiller.Create(type, 3)!;
            var expected = JsonSerializer.SerializeToUtf8Bytes(sample, type, old);
            var actual = JsonSerializer.SerializeToUtf8Bytes(sample, type, IpcJson.Options);
            if (!expected.AsSpan().SequenceEqual(actual)) problems.Add($"{type.Name}: bytes differ");
            var read = JsonSerializer.Deserialize(expected, type, IpcJson.Options);
            if (!expected.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(read, type, old))) problems.Add($"{type.Name}: read differs");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Shared_options_uses_generated_metadata_for_registered_types()
    {
        // A registered type resolves from the context (its info is a generated one, not the reflection resolver's).
        var info = IpcJson.Options.GetTypeInfo(typeof(JobDto));
        Assert.NotNull(info);
        Assert.NotNull(IpcJsonContext.Default.GetTypeInfo(typeof(JobDto)));
        Assert.IsType<JsonTypeInfo<JobDto>>(info, exactMatch: false);
        // The reflection fallback still serves a type the context doesn't know.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Unregistered { SomeValue = 3 }, IpcJson.Options);
        Assert.Equal("{\"someValue\":3}", Encoding.UTF8.GetString(bytes));
    }

    private sealed class Unregistered { public int SomeValue { get; set; } }

    [Fact]
    public void Every_registered_type_travels_in_an_envelope_with_the_old_bytes()
    {
        var old = OldOptions();
        var problems = new List<string>();
        foreach (var type in Registered().Where(t => t != typeof(IpcMessage)))
        {
            var sample = JsonSampleFiller.Create(type, 1)!;
            var oldMessage = new IpcMessage(Protocol.Version, "x.y", "0123456789abcdef", "reply1", JsonSerializer.SerializeToElement(sample, type, old));
            var expected = JsonSerializer.SerializeToUtf8Bytes(oldMessage, old);

            var create = typeof(IpcMessage).GetMethods().Single(m => m.Name == nameof(IpcMessage.Create) && m.IsGenericMethodDefinition)
                .MakeGenericMethod(type);
            var created = (IpcMessage)create.Invoke(null, ["x.y", sample, "reply1"])!;
            var actual = JsonSerializer.SerializeToUtf8Bytes(created with { Id = oldMessage.Id }, IpcJson.Options);
            if (!expected.AsSpan().SequenceEqual(actual)) { problems.Add($"{type.Name}: envelope bytes differ"); continue; }

            var read = JsonSerializer.Deserialize<IpcMessage>(expected, IpcJson.Options)!;
            var payload = typeof(IpcMessage).GetMethod(nameof(IpcMessage.GetPayload))!.MakeGenericMethod(type).Invoke(read, null);
            if (!JsonSerializer.SerializeToUtf8Bytes(payload, type, old).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(sample, type, old)))
                problems.Add($"{type.Name}: GetPayload differs");
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void Collection_and_primitive_payloads_match()
    {
        var old = OldOptions();
        AssertSame(new List<SegmentDto> { new() { Start = 0, End = 9 } }, old);
        AssertSame(new List<string> { "a", "b" }, old);
        AssertSame(new[] { "a", "b" }, old);
        AssertSame(new List<ConnectionTelemetryDto> { new() }, old);
        AssertSame(new List<DownloadGroupDto> { new() { Name = "g" } }, old);
    }

    private static void AssertSame<T>(T value, JsonSerializerOptions old)
    {
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(value, old), JsonSerializer.SerializeToUtf8Bytes(value, IpcJson.Options));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(value, old), JsonSerializer.SerializeToUtf8Bytes(value, IpcJsonContext.Default.Options));
        var json = JsonSerializer.SerializeToUtf8Bytes(value, old);
        Assert.Equal(json, JsonSerializer.SerializeToUtf8Bytes(JsonSerializer.Deserialize<T>(json, IpcJson.Options), old));
    }

    [Fact]
    public void Anonymous_payload_replacements_keep_their_json()
    {
        var oldEmpty = JsonSerializer.SerializeToUtf8Bytes(new { }, OldOptions());
        Assert.Equal(oldEmpty, JsonSerializer.SerializeToUtf8Bytes(new EmptyPayload(), IpcJson.Options));
        Assert.Equal("{}", Encoding.UTF8.GetString(oldEmpty));

        var oldFail = JsonSerializer.SerializeToUtf8Bytes(new { Ok = false, Error = "That." }, OldOptions());
        Assert.Equal(oldFail, JsonSerializer.SerializeToUtf8Bytes(new OkErrorPayload { Ok = false, Error = "That." }, IpcJson.Options));

        var oldError = JsonSerializer.SerializeToUtf8Bytes(new { errorCode = "message_too_large", error = "x" }, OldOptions());
        Assert.Equal(oldError, JsonSerializer.SerializeToUtf8Bytes(new ErrorPayload { ErrorCode = "message_too_large", Error = "x" }, IpcJson.Options));
        // A null member is omitted by both (the old anonymous object would have written the same).
        Assert.Equal("{\"ok\":true}", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(new OkErrorPayload { Ok = true }, IpcJson.Options)));
    }

    [Fact]
    public void Reading_edge_cases_behave_like_reflection()
    {
        var old = OldOptions();
        string[] inputs =
        [
            // member names in any case, unknown members ignored, null members, members missing (record constructor parameters)
            "{\"V\":1,\"TYPE\":\"a.b\",\"iD\":\"x\",\"unknown\":{\"deep\":[1,2]},\"payload\":{\"k\":\"v\"}}",
            "{\"type\":\"only\"}",
            "{}",
            "{\"v\":1,\"type\":null,\"id\":null,\"replyTo\":null,\"payload\":null}",
            "{\"v\":2,\"type\":\"t\",\"id\":\"i\",\"replyTo\":\"r\",\"payload\":[1,\"a\",null]}",
            "{\"v\":2,\"type\":\"t\",\"id\":\"i\",\"payload\":12345678901234567890}",
        ];
        foreach (var json in inputs)
        {
            var a = Read<IpcMessage>(json, old);
            var b = Read<IpcMessage>(json, IpcJson.Options);
            var c = Read<IpcMessage>(json, IpcJsonContext.Default.Options);
            Assert.Equal(Text(a), Text(b));
            Assert.Equal(Text(a), Text(c));
        }

        // A DTO read from a sparse object keeps its constructor-made defaults, and drops unknown members, the same way.
        foreach (var type in Registered().Where(t => t != typeof(IpcMessage)))
        {
            foreach (var json in new[] { "{}", "{\"unknownMember\":1,\"UNKNOWN2\":[1]}", "{\"ID\":\"x\",\"Name\":\"y\",\"url\":\"z\"}" })
            {
                object? a, b;
                string? errorA = null, errorB = null;
                try { a = JsonSerializer.Deserialize(json, type, old); } catch (JsonException e) { a = null; errorA = e.GetType().Name; }
                try { b = JsonSerializer.Deserialize(json, type, IpcJsonContext.Default.Options); } catch (JsonException e) { b = null; errorB = e.GetType().Name; }
                Assert.Equal(errorA, errorB);
                Assert.Equal(JsonSerializer.Serialize(a, type, old), JsonSerializer.Serialize(b, type, old));
            }
        }
    }

    private static string Text(IpcMessage? message) => message is null ? "null" : JsonSerializer.Serialize(message, IpcJson.Options) + "|" + message.V;

    private static T? Read<T>(string json, JsonSerializerOptions options)
    {
        try { return JsonSerializer.Deserialize<T>(json, options); }
        catch (JsonException) { return default; }
    }

    /// <summary>Generated metadata does not run property initializers for a member missing from the JSON when the type has init-only members.</summary>
    [Fact]
    public void Registered_dtos_have_no_init_only_members()
    {
        var offenders = // RemotePairResult: init-only members without initializers (null is their default either way), so nothing can differ.
        Registered().Where(t => t != typeof(IpcMessage) && t != typeof(RemotePairResult)).SelectMany(t => t.GetProperties())
            .Where(p => p.SetMethod?.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(System.Runtime.CompilerServices.IsExternalInit)) == true)
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}").ToList();
        Assert.True(offenders.Count == 0, "Use { get; set; } for: " + string.Join(", ", offenders));
    }

    [Fact]
    public void Wrong_shapes_are_rejected_the_same_way()
    {
        var old = OldOptions();
        foreach (var json in new[] { "[]", "\"x\"", "{\"v\":\"one\",\"type\":\"a\",\"id\":\"i\"}", "{\"v\":1,\"type\":5}" })
        {
            Exception? a = null, b = null;
            try { JsonSerializer.Deserialize<IpcMessage>(json, old); } catch (JsonException e) { a = e; }
            try { JsonSerializer.Deserialize<IpcMessage>(json, IpcJson.Options); } catch (JsonException e) { b = e; }
            Assert.Equal(a is null, b is null);
        }
    }

    [Fact]
    public void Null_members_are_omitted_and_json_element_payloads_round_trip()
    {
        var message = new IpcMessage(1, "a.b", "id1", null, null);
        Assert.Equal("{\"v\":1,\"type\":\"a.b\",\"id\":\"id1\"}", Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(message, IpcJson.Options)));
        var withPayload = message with { Payload = JsonDocument.Parse("{\"x\":[1,2.50,\"s\",null,true]}").RootElement.Clone() };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(withPayload, IpcJson.Options);
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(withPayload, OldOptions()), bytes);
        Assert.Contains("2.50", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void Enum_spelling_of_the_shared_options_is_camel_case_and_reads_names_in_any_case_and_numbers()
    {
        // The options' own converter (JsonStringEnumConverter with the camelCase policy) still decides every enum: the
        // generated metadata asks it, so what the wire says for an enum is unchanged.
        var old = OldOptions();
        foreach (var value in Enum.GetValues<SampleMode>())
        {
            Assert.Equal(JsonSerializer.Serialize(value, old), JsonSerializer.Serialize(value, IpcJson.Options));
        }

        Assert.Equal("\"readOnly\"", JsonSerializer.Serialize(SampleMode.ReadOnly, IpcJson.Options));
        foreach (var text in new[] { "\"readOnly\"", "\"ReadOnly\"", "\"READONLY\"", "2" })
        {
            Assert.Equal(JsonSerializer.Deserialize<SampleMode>(text, old), JsonSerializer.Deserialize<SampleMode>(text, IpcJson.Options));
        }

        Assert.Equal(JsonSerializer.Serialize(new Holder { Mode = SampleMode.ReadOnly }, old), JsonSerializer.Serialize(new Holder { Mode = SampleMode.ReadOnly }, IpcJson.Options));
        Assert.Equal(JsonSerializer.Serialize(new Dictionary<SampleMode, int> { [SampleMode.ReadOnly] = 1 }, old),
            JsonSerializer.Serialize(new Dictionary<SampleMode, int> { [SampleMode.ReadOnly] = 1 }, IpcJson.Options));
    }

    private enum SampleMode { None, Full, ReadOnly }
    private sealed class Holder { public SampleMode Mode { get; set; } }

    [Fact]
    public void Job_list_file_keeps_its_bytes_with_the_generated_context()
    {
        var webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true };
        var entries = new List<JobListEntry>
        {
            new() { Url = "https://example.test/a.zip", FileName = "a.zip", QueueId = 2, RequestedConnections = 4, ContentType = "application/zip",
                    Torrent = new TorrentListEntry { InfoHash = "abc", Name = "t", FileIndexes = [1, 3] } },
            new() { Url = "https://example.test/b.bin" },
        };
        var expected = JsonSerializer.Serialize(entries, webOptions);
        var job = new JobDto { Id = Guid.NewGuid(), Url = "https://example.test/a.zip", FileName = "a.zip" };
        // Round trip through the public API; the bytes of the full file are compared on a list built the old way.
        var viaFile = JobListFile.Deserialize(expected);
        Assert.Equal(2, viaFile.Count);
        Assert.Equal(expected, JsonSerializer.Serialize(viaFile.ToList(), webOptions));
        Assert.Contains("\"url\": \"https://example.test/a.zip\"", JobListFile.Serialize([job]));
    }
}
