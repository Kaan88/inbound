using System.Text.Json.Serialization;
using DownloadManager.Core;
using DownloadManager.Core.Archives;
using DownloadManager.Core.Providers;

namespace DownloadManager.Persistence;

/// <summary>
/// PERF.10: generated JSON metadata for every record the stores write with <c>PersistenceJson.Options</c> (job, checkpoint,
/// settings, torrent files, remote jobs and accounts, archive records). <b>Every new stored record is added here</b>; the
/// file format is unchanged, and <c>PersistenceJsonContextTests</c> compares the bytes with the reflection serializer's.
/// The options match <c>PersistenceJson.Options</c> (the enum spelling comes from the options' converter; the context
/// names none, which is why it is metadata only and not used on its own for a file with an enum yet). Types the
/// roots reach (the DTO-like Core models, their enums and collections) are generated too.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(DownloadJob))]
[JsonSerializable(typeof(DownloadCheckpoint))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(JsonJobStore.RequestClaim))]
[JsonSerializable(typeof(RemoteJobRecord))]
[JsonSerializable(typeof(RemoteAccountPacing))]
[JsonSerializable(typeof(StoredRemoteAccount))]
[JsonSerializable(typeof(List<TorrentFile>))]
[JsonSerializable(typeof(ArchiveSettings))]
[JsonSerializable(typeof(ArchiveSetRecord))]
[JsonSerializable(typeof(ArchivePackagePassword))]
[JsonSerializable(typeof(ArchiveSavedPasswords))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext
{
}
