using Android.Graphics;
using Android.Widget;
using DownloadManager.Contracts;
using DownloadManager.Mobile;
using Google.Android.Material.Dialog;

namespace Inbound.App.Screens;

internal sealed partial class RemoteFilesScreen
{
    private readonly HashSet<string> _changedSftpKeys = new(StringComparer.Ordinal);

    /// <summary>
    /// Reads every key the server offers and asks once about all of them (review R1: trusting only the first made rclone fail when it
    /// negotiated another). A key that really changed is shown against the one trusted before and needs a second, deliberate step (M1).
    /// <paramref name="trusted"/> runs after the keys are saved.
    /// </summary>
    private async Task ReviewSftpKeyAsync(SiteDto site, Action? trusted = null)
    {
        if (!SameHost() || _busy || !_secureHostConnection || _confirm is not null) return;
        int generation = ++_generation;
        _busy = true;
        _error = null;
        Render();
        SiteProbeResultDto? probe = null;
        try
        {
            var result = await _client.ProbeSftpAsync(site.Host, site.Port, _life.Token);
            if (!SameHost() || generation != _generation) return;
            if (result.Ok && result.Keys.Count > 0) probe = result;
            else _error = "Could not read an SFTP server key.";
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (SameHost() && generation == _generation) _error = "Could not read an SFTP server key.";
        }
        finally
        {
            if (!_closed && generation == _generation) { _busy = false; Render(); }
        }
        if (probe is null)
        {
            if (_error is not null && _inFolder) Toast.MakeText(_activity, _error, ToastLength.Long)?.Show();
            return;
        }
        if (!SameHost() || generation != _generation || _confirm is not null) return;
        var review = SftpKeyReview.Compare(site.HostKeys, probe.Keys);
        if (review.Case == SftpKeyCase.Unchanged) { _ = SaveSftpKeyAsync(site, review, generation, trusted); return; }
        ShowSftpKeyDialog(site, review, generation, trusted);
    }

    private TextView KeyLines(string heading, IEnumerable<SiteHostKeyDto> keys, int headingColor)
    {
        var text = new TextView(_activity) { TextSize = 12 };
        text.SetTypeface(Typeface.Monospace, TypefaceStyle.Normal);
        text.SetTextIsSelectable(true);
        text.SetTextColor(new Color(Attr(Resource.Attribute.colorOnSurface)));
        text.Text = heading + "\n" + string.Join("\n", keys.Select(key => key.Type + "\n" + SftpKeyReview.FingerprintLines(key.Fingerprint)));
        text.SetPadding(0, Dp(8), 0, 0);
        return text;
    }

    private void ShowSftpKeyDialog(SiteDto site, SftpKeyReview review, int reviewedGeneration, Action? trusted)
    {
        bool changed = review.Case == SftpKeyCase.Changed;
        var body = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        body.SetPadding(Dp(20), Dp(8), Dp(20), 0);
        var scroll = new ScrollView(_activity);
        var column = new LinearLayout(_activity) { Orientation = Orientation.Vertical };
        scroll.AddView(column);
        body.AddView(scroll, new LinearLayout.LayoutParams(-1, -2));
        column.AddView(FolderText($"{site.Name} · {site.Host}:{site.Port}\nHost: {_hostDisplay}", 14, Resource.Attribute.colorOnSurfaceVariant));
        if (changed)
        {
            var warning = FolderText("The server's key is not the one you trusted. This could mean the connection is being intercepted. "
                + "Only continue if the server's administrator confirms that the key changed.", 14, Resource.Attribute.colorError);
            warning.SetPadding(0, Dp(10), 0, 0);
            column.AddView(warning);
            column.AddView(KeyLines("Trusted before:", review.Trusted, Resource.Attribute.colorOnSurfaceVariant));
            column.AddView(KeyLines("Offered now:", review.Offered, Resource.Attribute.colorOnSurface));
        }
        else
        {
            if (review.Case == SftpKeyCase.MoreKeys)
            {
                var more = FolderText("The server also offers other key types. Trusting them lets this phone connect whichever one the server uses.", 14, Resource.Attribute.colorOnSurfaceVariant);
                more.SetPadding(0, Dp(10), 0, 0);
                column.AddView(more);
            }
            column.AddView(KeyLines("Keys the server offers:", review.Offered, Resource.Attribute.colorOnSurface));
            var compare = FolderText("Compare a fingerprint with the one provided by your server administrator before trusting it.", 14, Resource.Attribute.colorOnSurfaceVariant);
            compare.SetPadding(0, Dp(10), 0, 0);
            column.AddView(compare);
        }
        CheckBox? confirmed = null;
        if (changed)
        {
            confirmed = new CheckBox(_activity) { Text = "I checked the new key with the server's administrator", SaveEnabled = false };
            body.AddView(confirmed);
        }
        var dialog = new MaterialAlertDialogBuilder(_activity)
            .SetTitle(changed ? "SFTP server key changed" : review.Case == SftpKeyCase.MoreKeys ? "Trust more SFTP server keys?" : "Trust this SFTP server key?")!
            .SetView(body)!
            .SetNegativeButton("Cancel", (Android.Content.IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton(changed ? "Replace trusted keys" : review.Offered.Count > 1 ? "Trust keys" : "Trust key", (_, _) => _ = SaveSftpKeyAsync(site, review, reviewedGeneration, trusted))!
            .Create();
        _confirm = dialog;
        dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_confirm, dialog)) _confirm = null; };
        dialog.ShowEvent += (_, _) =>
        {
            if (confirmed is null) return;
            var accept = dialog.GetButton((int)Android.Content.DialogButtonType.Positive)!;
            accept.Enabled = false;
            confirmed.CheckedChange += (_, e) => accept.Enabled = e.IsChecked;
        };
        dialog.Show();
    }

    private async Task SaveSftpKeyAsync(SiteDto site, SftpKeyReview review, int reviewedGeneration, Action? trusted)
    {
        if (!SameHost() || !_secureHostConnection || _busy || reviewedGeneration != _generation) return;
        int generation = ++_generation;
        _busy = _mutation = true;
        _error = null;
        Render();
        bool saved = false;
        try
        {
            var result = await _client.TrustSftpEndpointAsync(site.Id, site.Host, site.Port, review.Entries, _life.Token);
            if (!SameHost() || generation != _generation) return;
            if (!result.Ok) _error = result.Message ?? "The Host did not accept the server key.";
            else
            {
                _changedSftpKeys.Remove(site.Id);
                saved = true;
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception)
        {
            if (SameHost() && generation == _generation) _error = "Could not confirm whether the Host saved the server key. Refresh the locations.";
        }
        finally
        {
            if (!_closed && generation == _generation) { _busy = _mutation = false; Render(); }
        }
        if (!saved) { if (_error is not null && _inFolder) Toast.MakeText(_activity, _error, ToastLength.Long)?.Show(); return; }
        await LoadLocationsAsync();
        if (SameHost()) trusted?.Invoke();
    }
}
