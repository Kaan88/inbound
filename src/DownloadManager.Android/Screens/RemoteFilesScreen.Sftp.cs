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

    private static string KeyText(SiteHostKeyDto key) => key.Type + "\n" + SftpKeyReview.FingerprintLines(key.Fingerprint);

    /// <summary>x26, x26b and x27: the key question as a sheet, with copy buttons and the two-step confirmation of a changed key.</summary>
    private void ShowSftpKeyDialog(SiteDto site, SftpKeyReview review, int reviewedGeneration, Action? trusted)
    {
        bool changed = review.Case == SftpKeyCase.Changed;
        var dialog = NewSheet(out var column);
        string title = changed ? "Server key changed"
            : review.Case == SftpKeyCase.MoreKeys ? "Trust more server keys?"
            : review.Offered.Count > 1 ? "Trust these server keys?" : "Trust this server key?";
        SheetTitle(column, title, $"{site.Name} · {site.Host}:{site.Port}", _phoneHost ? "On this phone" : "On " + _hostName);
        if (changed)
        {
            AddInset(column, Banner(BannerKind.Fail, "This is not the key you trusted",
                "Someone could be intercepting the connection. Only continue if the server's administrator confirms that the key changed.",
                Resource.Drawable.ic_remote_alert));
            foreach (var key in review.Trusted) AddInset(column, CopyBox(KeyText(key), "Copy trusted key", "Trusted before"));
            foreach (var key in review.Offered) AddInset(column, CopyBox(KeyText(key), "Copy offered key", "Offered now"));
        }
        else
        {
            if (review.Case == SftpKeyCase.MoreKeys)
                AddInset(column, FolderText("The server also offers other key types. Trusting them lets this phone connect whichever one the server uses.", 15,
                    Resource.Attribute.colorOnSurfaceVariant));
            foreach (var key in review.Offered) AddInset(column, CopyBox(KeyText(key), "Copy key"));
            AddInset(column, Banner(BannerKind.Info, "Compare a fingerprint with the one from the server's administrator before you trust it.", null,
                Resource.Drawable.ic_remote_info));
        }
        CheckBox? confirmed = null;
        if (changed)
        {
            confirmed = new CheckBox(_activity) { Text = "I checked the new key with the server's administrator", SaveEnabled = false };
            AddInset(column, confirmed);
        }
        string primaryText = changed ? "Replace trusted key" : review.Offered.Count > 1 ? "Trust keys" : "Trust key";
        var accept = SheetButtons(column, primaryText, Resource.Drawable.ic_settings_site_key,
            () => _ = SaveSftpKeyAsync(site, review, reviewedGeneration, trusted));
        if (confirmed is not null)
        {
            accept.Enabled = false;
            confirmed.CheckedChange += (_, e) => accept.Enabled = e.IsChecked;
        }
        _confirm = null;
        ShowSheet(dialog);
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
