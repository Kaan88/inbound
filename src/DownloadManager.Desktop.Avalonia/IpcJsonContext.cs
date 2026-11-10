using System.Text.Json;
using System.Text.Json.Serialization;
using DownloadManager.Contracts;

namespace DownloadManager.AvaloniaUi;

/// <summary>
/// Prototype only: source-generated metadata for the IPC types this window sends and reads, put in front of
/// <see cref="IpcJson.Options"/>' reflection resolver so the pipe works under Native AOT. The real fix belongs in
/// Contracts (one context for every DTO), not here.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, UseStringEnumConverter = true)]
[JsonSerializable(typeof(IpcMessage))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(SnapshotDto))]
[JsonSerializable(typeof(JobEventDto))]
[JsonSerializable(typeof(List<DownloadGroupDto>))]
[JsonSerializable(typeof(HostHelloDto))]
[JsonSerializable(typeof(ClientHelloDto))]
[JsonSerializable(typeof(AddJobRequest))]
[JsonSerializable(typeof(AddJobResponse))]
[JsonSerializable(typeof(JobActionRequest))]
[JsonSerializable(typeof(JobActionResponse))]
[JsonSerializable(typeof(GroupActionRequest))]
[JsonSerializable(typeof(SpeedHistoryDto))]
[JsonSerializable(typeof(UpdateProgressDto))]
[JsonSerializable(typeof(ArchiveChangedDto))]
internal sealed partial class IpcJsonContext : JsonSerializerContext
{
    /// <summary>Before the first message: the options lock on first use.</summary>
    public static void Install()
    {
        IpcJson.Options.TypeInfoResolverChain.Insert(0, Default);
        // Anything else (such as the empty anonymous payloads) still goes through reflection.
        if (IpcJson.Options.TypeInfoResolverChain.Count == 1) IpcJson.Options.TypeInfoResolverChain.Add(new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver());
    }
}
