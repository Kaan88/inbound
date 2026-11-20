#if INBOUND_RCLONE_PROBE
using System.Security.Cryptography;
using Android.Content;
using Android.Provider;
using Android.Util;

namespace Inbound.App;

/// <summary>
/// Probe-only synthetic upload source in shared Downloads. The URI is persisted so
/// the exact owned row can be removed by a later explicit cleanup intent.
/// </summary>
internal static class MediaStoreUploadProbe
{
    private const string Tag = "InboundRemoteFilesProbe";
    private const string PreferencesName = "rclone_media_upload_probe";
    private const string UriKey = "owned_uri";
    private static readonly byte[] Payload = "Inbound C4 synthetic upload probe\n"u8.ToArray();
    private static readonly object WorkLock = new();
    private static Task _work = Task.CompletedTask;

    private static void Enqueue(Action action)
    {
        lock (WorkLock)
            _work = _work.ContinueWith(_ => action(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public static void Create(Context context, bool nested = false) => Enqueue(() =>
    {
        try
        {
            var preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)!;
            if (preferences.Contains(UriKey))
                throw new InvalidOperationException("An upload probe is already owned; clean it up first.");

            var id = Guid.NewGuid().ToString("N");
            var name = $"inbound-c4-upload-probe-{id}.txt";
            var values = new ContentValues();
            values.Put(MediaStore.IMediaColumns.DisplayName, name);
            values.Put(MediaStore.IMediaColumns.MimeType, "text/plain");
            values.Put(MediaStore.IMediaColumns.RelativePath, nested ? $"Download/inbound-c4-{id}/" : "Download/");
            values.Put(MediaStore.IMediaColumns.IsPending, 1);
            var resolver = context.ContentResolver!;
            var uri = resolver.Insert(MediaStore.Downloads.ExternalContentUri, values)
                ?? throw new InvalidOperationException("MediaStore insert returned no URI.");
            try
            {
                if (!preferences.Edit()!.PutString(UriKey, uri.ToString()).Commit())
                    throw new InvalidOperationException("Could not persist probe cleanup URI.");
                using (var output = resolver.OpenOutputStream(uri, "w")
                    ?? throw new InvalidOperationException("Could not open pending MediaStore row."))
                    output.Write(Payload);

                var published = new ContentValues();
                published.Put(MediaStore.IMediaColumns.IsPending, 0);
                if (resolver.Update(uri, published, null, null) != 1)
                    throw new InvalidOperationException("Could not publish exactly the inserted MediaStore row.");

                var hash = Convert.ToHexString(SHA256.HashData(Payload)).ToLowerInvariant();
                Log.Info(Tag, $"UPLOAD_SOURCE_READY name={name} uri={uri} bytes={Payload.Length} sha256={hash}; cleanup before uninstall with media_upload_cleanup=true");
            }
            catch
            {
                resolver.Delete(uri, null, null);
                preferences.Edit()!.Remove(UriKey).Commit();
                throw;
            }
        }
        catch (Exception ex) { Log.Error(Tag, "UPLOAD_SOURCE_CREATE_FAILED " + ex); }
    });

    public static void Cleanup(Context context) => Enqueue(() =>
    {
        try
        {
            var preferences = context.GetSharedPreferences(PreferencesName, FileCreationMode.Private)!;
            var raw = preferences.GetString(UriKey, null);
            if (string.IsNullOrEmpty(raw))
            {
                Log.Info(Tag, "UPLOAD_SOURCE_CLEANUP_NONE");
                return;
            }
            var uri = Android.Net.Uri.Parse(raw);
            var deleted = context.ContentResolver!.Delete(uri, null, null);
            if (deleted != 1) throw new InvalidOperationException($"Owned URI cleanup affected {deleted} rows.");
            if (!preferences.Edit()!.Remove(UriKey).Commit())
                throw new InvalidOperationException("Deleted probe row but could not clear its cleanup record.");
            Log.Info(Tag, $"UPLOAD_SOURCE_CLEANED uri={uri}");
        }
        catch (Exception ex) { Log.Error(Tag, "UPLOAD_SOURCE_CLEANUP_FAILED " + ex); }
    });
}
#endif
