using Android.Views;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;

namespace Inbound.App.Screens;

internal sealed partial class RemoteFilesScreen
{
    private LocalFileEntry? _phoneUploadSource;
    private bool IsPhoneUploadSelected(LocalFileEntry item) => item.Uri is not null && _phoneUploadSource?.Uri?.Equals(item.Uri) == true;

    private bool CanUseUploadProbe()
    {
#if INBOUND_RCLONE_PROBE
        return _phoneHost && SameHost() && _showPhoneFiles && !_busy && !_mutation
            && _path is not (null or "/") && !string.IsNullOrEmpty(_listing)
            && _site is { Kind: SiteKindCatalog.S3, Provider: "Other" }
            && Uri.TryCreate(_site.Endpoint, UriKind.Absolute, out var endpoint)
            && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.Host == "127.0.0.1";
#else
        return false;
#endif
    }

    private void BindPhoneUploadRow(View card, LocalFileEntry item)
    {
        if (!CanUseUploadProbe() || item.Uri is null || item.Size is < 0 or > 64 * 1024) return;
        bool selected = IsPhoneUploadSelected(item);
        card.Selected = selected;
        card.Focusable = card.Clickable = true;
        card.ContentDescription = $"{(selected ? "Deselect" : "Select")} {item.Name}, on this phone.";
        card.Click += (_, _) =>
        {
            if (!CanUseUploadProbe()) return;
            _phoneUploadSource = selected ? null : item;
            RenderFolderC4();
        };
    }

    private void RenderPhoneUploadFooter()
    {
        if (!CanUseUploadProbe() || _site is null) return;
        var footer = new LinearLayout(_activity) { Orientation = Orientation.Horizontal };
        footer.SetGravity(GravityFlags.CenterVertical);
        var info = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        info.AddView(FolderText(_phoneUploadSource is { } source ? $"1 file · {Fmt.Bytes(source.Size)}" : "0 files", 16, Resource.Attribute.colorOnSurface));
        info.AddView(FolderText($"To {_site.Name} · {_path}", 12, Resource.Attribute.colorOnSurfaceVariant));
        footer.AddView(info, new LinearLayout.LayoutParams(0, -2, 1));
        var send = new MaterialButton(_activity) { Text = "To site", Enabled = _phoneUploadSource is not null };
        send.SetAllCaps(false);
        send.Click += (_, _) => ConfirmPhoneUpload();
        footer.AddView(send, new LinearLayout.LayoutParams(-2, Motion.Dp(_activity, 56)));
        _folderFooter!.AddView(footer);
    }

    private void ConfirmPhoneUpload()
    {
        if (!CanUseUploadProbe() || _confirm is not null || _phoneUploadSource is not { Uri: { } uri } source
            || _site is not { } site || _path is not { } folder) return;
        string? listing = _listing;
        int generation = _generation;
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle("Upload file?")!
            .SetMessage($"{source.Name}\n\nFrom this phone to {site.Name} · {folder}\nHost: {_hostDisplay}\nExisting files won't be replaced.")!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton("Upload", (_, _) =>
            {
                if (!CanUseUploadProbe() || generation != _generation || listing != _listing || _phoneUploadSource != source) return;
                _ = SendPhoneUploadAsync(uri, new SiteDeviceOpenRequest
                { Id = site.Id, Folder = folder, Name = source.Name, Listing = listing, Size = source.Size });
            })!.Create();
        _confirm.DismissEvent += (_, _) => _confirm = null;
        _confirm.Show();
    }

    private async Task SendPhoneUploadAsync(Android.Net.Uri uri, SiteDeviceOpenRequest destination)
    {
        _busy = _mutation = true;
        _phoneUploadSource = null;
        RenderFolderC4();
        string status;
        try
        {
            using var stream = _activity.ContentResolver?.OpenInputStream(uri);
            if (stream is null) status = "This phone file is no longer readable.";
            else
            {
                var result = await _client.WriteDeviceFileAsync(destination, stream, _life.Token);
                status = result.Ok ? "File uploaded." : result.Message ?? "The Host refused the upload.";
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { return; }
        catch (Exception) { status = "Upload not confirmed. Check the destination before trying again."; }
        finally { _busy = _mutation = false; }
        if (!SameHost()) return;
        SetPhoneTab(false);
        await BrowseAsync(_path, append: false, refresh: true);
        if (SameHost()) Toast.MakeText(_activity, status, ToastLength.Long)?.Show();
    }
}
