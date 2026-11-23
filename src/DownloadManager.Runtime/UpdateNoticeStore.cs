using System.Text.Json;
using DownloadManager.Runtime.WebAuth;

namespace DownloadManager.Runtime;

/// <summary>
/// UPD.D1: the server update notice's dismiss state, kept in <c>update-notice.json</c> in the state folder so "Not now"
/// and "Skip this version" survive a restart. A file that can't be read counts as none; one that can't be written keeps
/// the state for this run. Written with <see cref="AtomicFile"/>, like the other small durable files.
/// </summary>
public sealed class UpdateNoticeStore
{
    public const string FileName = "update-notice.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly Action<string>? _warn;
    private readonly object _sync = new();
    private UpdateNoticeState? _state;

    public UpdateNoticeStore(string stateDirectory, Action<string>? warn = null)
    {
        _path = Path.Combine(stateDirectory, FileName);
        _warn = warn;
    }

    /// <summary>The stored state, read once and then kept; <see cref="UpdateNoticeState.None"/> when there is none.</summary>
    public UpdateNoticeState Load()
    {
        lock (_sync)
        {
            if (_state is not null) return _state;
            _state = UpdateNoticeState.None;
            try
            {
                if (File.Exists(_path) &&
                    JsonSerializer.Deserialize<UpdateNoticeState>(File.ReadAllBytes(_path), Json) is { } stored)
                    _state = stored;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _warn?.Invoke($"Couldn't read the update notice state: {ex.GetType().Name}");
            }

            return _state;
        }
    }

    /// <summary>Replaces the stored state. A failure keeps the state for this run.</summary>
    public void Save(UpdateNoticeState state)
    {
        lock (_sync)
        {
            _state = state;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                AtomicFile.Write(_path, JsonSerializer.SerializeToUtf8Bytes(state, Json));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warn?.Invoke($"Couldn't save the update notice state: {ex.GetType().Name}");
            }
        }
    }
}
