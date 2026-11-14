using System.Text.Json.Serialization;
using DownloadManager.Contracts;

namespace DownloadManager.Cli;

/// <summary>One row of <c>add-batch --json</c> (PERF.10: a named type in place of an anonymous object, same JSON).</summary>
internal sealed record AddBatchRow(string Url, bool Accepted, bool Duplicate, string? Error, JobDto? Job);

/// <summary>One row of <c>folders --json</c>; the names are the JSON's (camelCase) and a missing value is left out.</summary>
internal sealed record FolderRow(string Kind, string? Name, string Path, long? FreeBytes, long? TotalBytes, bool InsideContainer);

/// <summary>
/// Generated metadata for the rows only the CLI prints. The options match <c>IpcJson.Options</c> (the naming and null
/// handling are written into the generated code, so they have to be repeated here). Everything else the CLI prints is
/// in <see cref="IpcJsonContext"/>; see <c>Program.PrettyJson</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<AddBatchRow>))]
[JsonSerializable(typeof(List<FolderRow>))]
internal sealed partial class CliJsonContext : JsonSerializerContext
{
}
