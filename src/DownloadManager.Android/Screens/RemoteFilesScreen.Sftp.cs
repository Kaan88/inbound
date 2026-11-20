using DownloadManager.Contracts;
using Google.Android.Material.Dialog;

namespace Inbound.App.Screens;

internal sealed partial class RemoteFilesScreen
{
    private readonly HashSet<string> _changedSftpKeys = new(StringComparer.Ordinal);

    private async Task ReviewSftpKeyAsync(SiteDto site)
    {
        if (!SameHost() || _busy || !_secureHostConnection || _confirm is not null) return;
        int generation = ++_generation;
        _busy = true;
        _error = null;
        Render();
        SiteHostKeyDto? key = null;
        try
        {
            var result = await _client.ProbeSftpAsync(site.Host, site.Port, _life.Token);
            if (!SameHost() || generation != _generation) return;
            if (result.Ok) key = result.Keys.FirstOrDefault();
            if (key is null) _error = "Could not read an SFTP server key.";
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
        if (key is null || !SameHost() || generation != _generation || _confirm is not null) return;
        bool changed = site.HostKeys.Count > 0;
        string warning = changed ? "The previously trusted key no longer matches. This could mean the connection is being intercepted.\n\n" : "";
        _confirm = new MaterialAlertDialogBuilder(_activity)
            .SetTitle(changed ? "SFTP server key changed" : "Trust this SFTP server key?")!
            .SetMessage($"{site.Name} · {site.Host}:{site.Port}\nHost: {_hostDisplay}\n\n{warning}{key.Type} · {key.Fingerprint}\n\nCompare this fingerprint with the one provided by your server administrator before trusting it.")!
            .SetNegativeButton("Cancel", (_, _) => { })!
            .SetPositiveButton("Trust key", (_, _) => _ = SaveSftpKeyAsync(site, key, generation))!
            .Create();
        _confirm.DismissEvent += (_, _) => _confirm = null;
        _confirm.Show();
    }

    private async Task SaveSftpKeyAsync(SiteDto site, SiteHostKeyDto key, int reviewedGeneration)
    {
        if (!SameHost() || !_secureHostConnection || _busy || reviewedGeneration != _generation) return;
        int generation = ++_generation;
        _busy = _mutation = true;
        _error = null;
        Render();
        try
        {
            var result = await _client.TrustSftpEndpointAsync(site.Id, site.Host, site.Port, key.Entry, _life.Token);
            if (!SameHost() || generation != _generation) return;
            if (!result.Ok) _error = result.Message ?? "The Host did not accept the server key.";
            else
            {
                _changedSftpKeys.Remove(site.Id);
                _busy = _mutation = false;
                _ = LoadLocationsAsync();
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
    }
}
