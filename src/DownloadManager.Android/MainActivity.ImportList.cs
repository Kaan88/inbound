using Android.Content;
using Android.OS;
using Android.Provider;
using DownloadManager.Mobile.Lists;
using DownloadManager.Mobile.Remote;
using Google.Android.Material.Dialog;
using Inbound.App.Screens;

namespace Inbound.App;

/// <summary>Import a list (A4's last row): the file chooser, the review page, the "not a list" dialog and the result on Downloads.</summary>
public sealed partial class MainActivity
{
    private ImportListScreen? _importList;

    /// <summary>The shown Host is connected, so a list can be sent to it (a paired PC that is away can't take one).</summary>
    internal bool HasImportList => _model is { Disposed: false } && _connection is not null
        && (_connection is not IRemoteStatus remote || remote.State.Status == RemoteConnectionStatus.Connected);

    /// <summary>The Add sheet's "Import a list": the chooser first, then the review page.</summary>
    internal void StartImportList()
    {
        if (_nav is null || _nav.PageOpen || !HasImportList) return;
        ChooseImportList(null);
    }

    /// <summary>Opens the chooser. <paramref name="screen"/> is the review page when a file is being replaced.</summary>
    internal void ChooseImportList(ImportListScreen? screen)
    {
        if (!PickListFile(uri => { if (uri is not null) ReadImportList(uri, screen); }))
            ShowMessage("No file chooser is available on this phone.");
    }

    /// <summary>Opens the system's chooser for a saved list (any type: an exported list may come with another MIME type).</summary>
    internal bool PickListFile(Action<Android.Net.Uri?> picked)
    {
        if (_torrentPicker is null) return false;
        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        // The same launcher and result as the .torrent chooser: only one chooser is ever open.
        _torrentPicked = picked; _pickKind = "list";
        try { _torrentPicker.Launch(intent); }
        catch (ActivityNotFoundException) { _torrentPicked = null; _pickKind = null; return false; }
        return true;
    }

    private void ReadImportList(Android.Net.Uri uri, ImportListScreen? screen)
    {
        var resolver = ContentResolver!;
        _ = Task.Run(() =>
        {
            byte[]? bytes = null;
            var name = "list file";
            string? failure = null;
            try
            {
                using (var cursor = resolver.Query(uri, [IOpenableColumns.DisplayName], null, null, null))
                    if (cursor is not null && cursor.MoveToFirst() && cursor.GetString(0) is { Length: > 0 } shown) name = shown;
                using var stream = resolver.OpenInputStream(uri) ?? throw new IOException();
                using var memory = new MemoryStream();
                var buffer = new byte[16384];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    memory.Write(buffer, 0, read);
                    if (memory.Length > ListImportModel.MaxFileBytes) { failure = ListImportModel.TooLarge; break; }
                }

                if (failure is null) bytes = memory.ToArray();
            }
            catch (Exception e) when (e is IOException or Java.Lang.SecurityException or UnauthorizedAccessException or InvalidOperationException)
            {
                failure = "That file could not be read.";
            }

            RunOnUiThread(() => ImportListRead(bytes, name, failure, screen));
        });
    }

    private void ImportListRead(byte[]? bytes, string name, string? failure, ImportListScreen? screen)
    {
        if (IsFinishing || IsDestroyed || _model is not { Disposed: false } model || _nav is null) return;
        if (failure is not null || bytes is null) { ShowMessage(failure ?? "That file could not be read."); return; }
        var list = ListImportModel.TryRead(bytes, name, Capabilities.IsRemote, HostName);
        // The review page it was chosen from may have closed meanwhile.
        var open = screen is not null && ReferenceEquals(_importList, screen) && _nav.PageOpen ? screen : null;
        if (list is null) { ShowNotAList(name, open); return; }
        if (open is not null) { open.Load(list); return; }
        // Not HasImportList: the chooser sends the app to the background, and a paired PC's connection is often still
        // coming back when the file arrives. Sending says so if it hasn't by then.
        if (_nav.PageOpen || _connection is null) return;
        _importList = new ImportListScreen(this, model, HasTorrents ? NewTorrentClient() : null, list);
        _nav.Open(_importList.Root, null, null, OnPageClosed, "Import a list");
    }

    private void ShowNotAList(string name, ImportListScreen? screen) =>
        new MaterialAlertDialogBuilder(this).SetTitle(ListImportModel.NotAListTitle)!
            .SetMessage($"{name} has no downloads Inbound can read. Export a list from Inbound on a computer or in the web UI, then import that file.")!
            .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Choose another file", (_, _) => ChooseImportList(screen))!.Show();

    /// <summary>The send is done: back on Downloads, with what was added and, when something was skipped, Details.</summary>
    internal void FinishImport(Android.Views.View page, ListImportResult result)
    {
        if (IsFinishing || IsDestroyed) return;
        CloseRowPage(page);
        // The page is still open while it animates away; the message waits so it sits above the bottom bar.
        ShowMessageAfterClose(result.SnackbarText, result.ActionText, result.ActionText is null ? null : () => ShowSkipped(result));
    }

    private void ShowSkipped(ListImportResult result)
    {
        if (IsFinishing || IsDestroyed) return;
        new MaterialAlertDialogBuilder(this).SetTitle(ListImportResult.DetailsTitle)!
            .SetMessage(result.DetailsBody)!
            .SetPositiveButton("OK", (IDialogInterfaceOnClickListener?)null)!.Show();
    }
}
