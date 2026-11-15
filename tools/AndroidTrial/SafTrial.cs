using Process = System.Diagnostics.Process;
using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text.Json.Nodes;
using Android.App;
using Android.Content;
using Android.Provider;
using Microsoft.Win32.SafeHandles;
using Uri = Android.Net.Uri;

namespace AndroidTrial;

/// <summary>
/// mode=saf: what a folder picked with ACTION_OPEN_DOCUMENT_TREE allows, without touching the engine.
/// phase=pick (default): launches the picker unless a grant is saved, takes the persistable grant, runs the checks.
/// phase=verify: after am force-stop, shows the saved grant survived and that a new document can be created and written in it.
/// </summary>
internal sealed class SafTrial(Activity activity, Action<string> say)
{
    private const int PickRequest = 9001;
    private string _runId = "saf";
    private string _phase = "pick";
    private JsonObject _json = new();

    private string GrantFile => Path.Combine(activity.FilesDir!.AbsolutePath, "saf-tree.txt");

    public void Begin(Intent intent)
    {
        _runId = intent.GetStringExtra("runId") ?? "saf";
        _phase = intent.GetStringExtra("phase") ?? "pick";
        _json = new JsonObject { ["runId"] = _runId, ["phase"] = _phase, ["api"] = (int)Android.OS.Build.VERSION.SdkInt };
        if (_phase == "pick" && !intent.GetBooleanExtra("repick", false) && File.Exists(GrantFile) && HasGrant(File.ReadAllText(GrantFile).Trim()))
            _phase = "verify";
        if (_phase == "verify")
        {
            var saved = File.ReadAllText(GrantFile).Trim();
            _ = Task.Run(() => RunAsync(Uri.Parse(saved)!));
            return;
        }

        var pick = new Intent(Intent.ActionOpenDocumentTree);
        pick.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission | ActivityFlags.GrantPersistableUriPermission | ActivityFlags.GrantPrefixUriPermission);
        say("launching ACTION_OPEN_DOCUMENT_TREE: waiting for a tap (pick or create a folder under Documents/Download, then 'Use this folder', 'Allow')");
        activity.StartActivityForResult(pick, PickRequest);
    }

    public void OnResult(int requestCode, Result resultCode, Intent? data)
    {
        if (requestCode != PickRequest) return;
        if (resultCode != Result.Ok || data?.Data is not { } tree)
        {
            say("picker cancelled: " + resultCode);
            Save();
            return;
        }

        try
        {
            activity.ContentResolver!.TakePersistableUriPermission(tree, ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantWriteUriPermission);
            File.WriteAllText(GrantFile, tree.ToString());
            _json["takePersistable"] = "ok";
        }
        catch (Exception ex) { _json["takePersistable"] = ex.ToString(); }

        _ = Task.Run(() => RunAsync(tree));
    }

    private bool HasGrant(string uri) =>
        activity.ContentResolver!.PersistedUriPermissions!.Any(p => p.IsWritePermission && p.Uri?.ToString() == uri);

    private void Save()
    {
        var file = Path.Combine(activity.GetExternalFilesDir(null)!.AbsolutePath, $"saf-result-{_runId}-{_phase}.json");
        File.WriteAllText(file, _json.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        say("RESULT " + _json.ToJsonString());
    }

    private static void Step(JsonObject o, string name, Func<object?> action)
    {
        try { var r = action(); o[name] = r is null ? "ok" : r.ToString(); }
        catch (Exception ex) { o[name] = "EXCEPTION " + ex.GetType().FullName + ": " + ex.Message; }
    }

    private async Task RunAsync(Uri tree)
    {
        await Task.Yield();
        var resolver = activity.ContentResolver!;
        try
        {
            _json["treeUri"] = tree.ToString();
            var grants = new JsonArray();
            foreach (var p in resolver.PersistedUriPermissions!) grants.Add($"{p.Uri} read={p.IsReadPermission} write={p.IsWritePermission}");
            _json["persistedGrants"] = grants;
            var treeId = DocumentsContract.GetTreeDocumentId(tree)!;
            var treeDoc = DocumentsContract.BuildDocumentUriUsingTree(tree, treeId)!;
            _json["treeDocumentId"] = treeId;

            var children = new JsonArray();
            Step(_json, "listChildren", () =>
            {
                var childUri = DocumentsContract.BuildChildDocumentsUriUsingTree(tree, treeId)!;
                using var c = resolver.Query(childUri, [DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnSize], null, null, null);
                while (c is not null && c.MoveToNext()) children.Add($"{c.GetString(0)} ({c.GetLong(1)} bytes)");
                return children.Count + " entries";
            });
            _json["children"] = children;

            var scratch = new JsonObject();
            _json["checks"] = scratch;
            var jobId = Guid.NewGuid().ToString("N");
            var partName = $"saf-test.bin.{jobId}.part";
            Uri? doc = null;
            Step(scratch, "1_createDocument", () => { doc = DocumentsContract.CreateDocument(resolver, treeDoc, "application/octet-stream", partName); return doc?.ToString(); });
            scratch["1_displayNameAfterCreate"] = doc is null ? null : DisplayName(resolver, doc);
            if (doc is null) { Save(); return; }

            const long FarOffset = 200L << 20;
            var pattern = new byte[4096];
            new Random(1).NextBytes(pattern);
            Step(scratch, "2_openWriteFarOffset", () =>
            {
                using var handle = OpenHandle(resolver, doc!, "rw");
                RandomAccess.SetLength(handle, FarOffset + 8192);
                RandomAccess.Write(handle, pattern, FarOffset);
                RandomAccess.Write(handle, pattern, 0);
                RandomAccess.FlushToDisk(handle);
                return "length now " + RandomAccess.GetLength(handle);
            });
            Step(scratch, "2b_fileStreamWrapper", () =>
            {
                using var fs = new FileStream(OpenHandle(resolver, doc!, "rw"), FileAccess.ReadWrite, 1, false);
                fs.SetLength(FarOffset + 16384);
                RandomAccess.Write(fs.SafeFileHandle, pattern, FarOffset + 8192);
                fs.Flush(true);
                return "FileStream.Length " + fs.Length + " canSeek " + fs.CanSeek;
            });
            Step(scratch, "3_readBackVerify", () =>
            {
                using var handle = OpenHandle(resolver, doc!, "r");
                var len = RandomAccess.GetLength(handle);
                var a = new byte[4096]; var b = new byte[4096]; var c = new byte[4096]; var hole = new byte[4096];
                RandomAccess.Read(handle, a, 0); RandomAccess.Read(handle, b, FarOffset); RandomAccess.Read(handle, c, FarOffset + 8192); RandomAccess.Read(handle, hole, 1 << 20);
                return $"length {len}; atZero={a.AsSpan().SequenceEqual(pattern)} atFar={b.AsSpan().SequenceEqual(pattern)} atFar+8192={c.AsSpan().SequenceEqual(pattern)} holeIsZero={hole.All(x => x == 0)}";
            });
            Step(scratch, "3b_reopenRwKeepsData", () =>
            {
                using var handle = OpenHandle(resolver, doc!, "rw");
                var a = new byte[4096];
                RandomAccess.Read(handle, a, FarOffset);
                return $"length {RandomAccess.GetLength(handle)} dataKept={a.AsSpan().SequenceEqual(pattern)}";
            });
            Uri? renamed = null;
            var finalName = "saf-test.bin";
            Step(scratch, "4_renameDocument", () => { renamed = DocumentsContract.RenameDocument(resolver, doc!, finalName); return renamed?.ToString() ?? "null (provider refused)"; });
            if (renamed is not null)
            {
                var r2 = renamed;
                scratch["4_displayNameAfterRename"] = DisplayName(resolver, r2);
                scratch["4_sameUriAfterRename"] = r2.ToString() == doc.ToString();
                Step(scratch, "4_bytesIntactAfterRename", () =>
                {
                    using var handle = OpenHandle(resolver, r2, "r");
                    var b = new byte[4096];
                    RandomAccess.Read(handle, b, FarOffset);
                    return $"length {RandomAccess.GetLength(handle)} dataKept={b.AsSpan().SequenceEqual(pattern)}";
                });
                var d2 = doc;
                Step(scratch, "4_oldUriStillOpens", () => { using var h = OpenHandle(resolver, d2, "r"); return "yes"; });
            }

            Uri? second = null, third = null;
            Step(scratch, "5a_createWithExistingName", () => { second = DocumentsContract.CreateDocument(resolver, treeDoc, "application/octet-stream", finalName); return second is null ? "null" : "created as " + DisplayName(resolver, second); });
            Step(scratch, "5b_renameOntoExistingName", () =>
            {
                third = DocumentsContract.CreateDocument(resolver, treeDoc, "application/octet-stream", "saf-collide.part");
                var r = DocumentsContract.RenameDocument(resolver, third!, finalName);
                return r is null ? "null (refused)" : "renamed to " + DisplayName(resolver, r);
            });

            var path = PathOf(treeId);
            scratch["6_pathGuess"] = path;
            if (path is not null)
            {
                Step(scratch, "6a_fileStreamByPathInPickedFolder", () => { using var fs = new FileStream(Path.Combine(path, "by-path.part"), FileMode.Create, FileAccess.ReadWrite); fs.WriteByte(1); return "created"; });
                Step(scratch, "6b_fileMoveByPath", () => { File.Move(Path.Combine(path, "by-path.part"), Path.Combine(path, "by-path.bin"), true); return "moved"; });
                try { File.Delete(Path.Combine(path, "by-path.bin")); File.Delete(Path.Combine(path, "by-path.part")); } catch (Exception) { }
            }

            if (_phase == "pick")
            {
                var bench = new JsonObject();
                _json["benchmarks"] = bench;
                var ext = activity.GetExternalFilesDir(null)!.AbsolutePath;
                for (var round = 1; round <= 2; round++)
                {
                    var rd = round;
                    Uri? benchDoc = null;
                    Step(bench, $"r{rd}_fd_sequential", () =>
                    {
                        benchDoc = DocumentsContract.CreateDocument(resolver, treeDoc, "application/octet-stream", $"bench-{rd}.part");
                        using var handle = OpenHandle(resolver, benchDoc!, "rw");
                        return Throughput(handle, 256, 1);
                    });
                    Step(bench, $"r{rd}_fd_8parallel_ranges", () =>
                    {
                        using var handle = OpenHandle(resolver, benchDoc!, "rw");
                        return Throughput(handle, 256, 8);
                    });
                    Step(bench, $"r{rd}_path_sequential_appExternalDir", () =>
                    {
                        var p = Path.Combine(ext, $"bench-{rd}.part");
                        string r;
                        using (var handle = File.OpenHandle(p, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.RandomAccess)) r = Throughput(handle, 256, 1);
                        File.Delete(p);
                        return r;
                    });
                    Step(bench, $"r{rd}_path_8parallel_appExternalDir", () =>
                    {
                        var p = Path.Combine(ext, $"bench8-{rd}.part");
                        string r;
                        using (var handle = File.OpenHandle(p, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite, FileOptions.RandomAccess)) r = Throughput(handle, 256, 8);
                        File.Delete(p);
                        return r;
                    });
                    var bd = benchDoc;
                    if (bd is not null) Step(bench, $"r{rd}_cleanupBenchDoc", () => DocumentsContract.DeleteDocument(resolver, bd));
                }
            }

            foreach (var u in new[] { doc, renamed, second, third })
            {
                if (u is null) continue;
                try { DocumentsContract.DeleteDocument(resolver, u); } catch (Exception) { }
            }
        }
        catch (Exception ex) { _json["fatal"] = ex.ToString(); say("FATAL " + ex); }

        Save();
    }

    private static string Throughput(SafeFileHandle handle, int megabytes, int writers)
    {
        var chunk = new byte[1 << 20];
        new Random(2).NextBytes(chunk);
        long total = (long)megabytes << 20;
        RandomAccess.SetLength(handle, total);
        var sw = Stopwatch.StartNew();
        var cpu0 = Process.GetCurrentProcess().TotalProcessorTime;
        var per = total / writers;
        Parallel.For(0, writers, new ParallelOptions { MaxDegreeOfParallelism = writers }, w =>
        {
            for (long off = w * per; off < (w + 1) * per; off += chunk.Length) RandomAccess.Write(handle, chunk, off);
        });
        var written = sw.Elapsed.TotalSeconds;
        RandomAccess.FlushToDisk(handle);
        var all = sw.Elapsed.TotalSeconds;
        var cpu = (Process.GetCurrentProcess().TotalProcessorTime - cpu0).TotalSeconds;
        return $"{megabytes} MiB, {writers} writer(s): write {written:F2}s ({megabytes / written:F0} MiB/s), +flush {all:F2}s ({megabytes / all:F0} MiB/s), cpu {cpu:F2}s";
    }

    private static SafeFileHandle OpenHandle(ContentResolver resolver, Uri uri, string mode)
    {
        var pfd = resolver.OpenFileDescriptor(uri, mode) ?? throw new IOException("OpenFileDescriptor returned null");
        var fd = pfd.DetachFd();
        pfd.Dispose();
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private static string? DisplayName(ContentResolver resolver, Uri uri)
    {
        using var c = resolver.Query(uri, [DocumentsContract.Document.ColumnDisplayName, DocumentsContract.Document.ColumnSize], null, null, null);
        return c is not null && c.MoveToFirst() ? $"{c.GetString(0)} ({c.GetLong(1)} bytes)" : null;
    }

    /// <summary>"primary:Documents/x" is /storage/emulated/0/Documents/x on the primary volume; any other volume is not guessed.</summary>
    private static string? PathOf(string documentId) =>
        documentId.StartsWith("primary:", StringComparison.Ordinal) ? Path.Combine("/storage/emulated/0", documentId["primary:".Length..]) : null;
}
