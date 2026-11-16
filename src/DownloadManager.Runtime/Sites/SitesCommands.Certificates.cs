using System.Security.Cryptography.X509Certificates;
using System.Text;
using DownloadManager.Contracts;
using DownloadManager.Sites;

namespace DownloadManager.Runtime.Sites;

/// <summary>
/// SITES.6b: trusting an FTPS or https server's own certificate (remote-sites.md, "FTPS certificates"). rclone has no
/// per-site CA and reads its CA file only at start, so the Host keeps one file of every certificate the user trusted, with
/// the system's roots (the file replaces them for rclone), and starts rclone again once it is idle when the file changes.
/// rclone's own check is never switched off.
/// </summary>
public sealed partial class SitesCommands
{
    private static readonly TimeSpan CertificateProbeTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// One writer of the trusted-certificates file at a time, so the last write has the newest list. Not <c>_gate</c>, which
    /// <see cref="AcquireAsync"/> holds while it writes the file.
    /// </summary>
    private readonly SemaphoreSlim _certificateFileGate = new(1, 1);

    /// <summary>The server's certificate for the "Trust this certificate?" question; no login.</summary>
    private async Task<SiteCertificateResultDto> CertificateAsync(string id, CancellationToken cancellationToken)
    {
        if (!SiteStore.IsId(id) || await _sites.GetAsync(id, cancellationToken).ConfigureAwait(false) is not { } site)
            return new SiteCertificateResultDto { Message = "That site no longer exists." };
        if (CertificateProblem(site) is { } problem) return new SiteCertificateResultDto { Message = problem };
        try
        {
            var info = await _certificateProbe(site, cancellationToken).ConfigureAwait(false);
            return new SiteCertificateResultDto
            {
                Ok = true,
                Certificate = ToDto(info),
                TrustedBySite = site.TrustedCertificates.Any(c => c.Fingerprint == info.Fingerprint),
            };
        }
        catch (TlsProbeException e)
        {
            return new SiteCertificateResultDto { Message = e.Message };
        }
    }

    /// <summary>Why a site has no certificate of its own to trust, or null.</summary>
    private static string? CertificateProblem(SiteRecord site) => site.Kind switch
    {
        SiteKinds.Sftp => "An SFTP site has a server key, not a certificate.",
        SiteKinds.Ftp when site.Tls == SiteTlsModes.None => "Plain FTP has no certificate: nothing is encrypted.",
        SiteKinds.WebDav when !SiteKindCatalog.Encrypted(site.Kind, null, site.Url, null) => "An http:// address has no certificate: nothing is encrypted.",
        SiteKinds.S3 when string.IsNullOrEmpty(site.Endpoint) => "Amazon S3's certificate is trusted by every PC; there is nothing to trust here.",
        SiteKinds.S3 when !SiteKindCatalog.Encrypted(site.Kind, null, null, site.Endpoint) => "An http:// address has no certificate: nothing is encrypted.",
        _ => null,
    };

    private static Task<TlsCertificateInfo> ProbeCertificateAsync(SiteRecord site, CancellationToken cancellationToken) =>
        TlsCertificateProbe.ProbeAsync(site.Host, site.Port, explicitFtp: site.Kind == SiteKinds.Ftp && site.Tls == SiteTlsModes.Explicit,
            CertificateProbeTimeout, cancellationToken);

    private static SiteCertificateDto ToDto(TlsCertificateInfo info) => new()
    {
        Subject = info.Subject,
        Issuer = info.Issuer,
        NotBefore = info.NotBefore,
        NotAfter = info.NotAfter,
        Fingerprint = info.Fingerprint,
        Names = [.. info.Names],
        SelfSigned = info.SelfSigned,
        TrustedBySystem = info.TrustedBySystem,
        CannotTrust = info.CannotTrust,
    };

    /// <summary>
    /// <c>sites.trust</c> for a TLS site: the certificate is read again and must be the one the user saw; it is added to the
    /// site (or, with <see cref="SiteTrustRequest.ForgetCertificates"/>, the site's are forgotten), and the file rewritten.
    /// </summary>
    private async Task<SiteResultDto> TrustCertificateAsync(SiteRecord site, SiteTrustRequest request, CancellationToken cancellationToken)
    {
        SiteCertificateRecord? added = null;
        if (!request.ForgetCertificates)
        {
            if (CertificateProblem(site) is { } problem) return Fail(problem);
            if (string.IsNullOrWhiteSpace(request.CertificateFingerprint)) return Fail("Say which certificate to trust.");
            TlsCertificateInfo info;
            try
            {
                info = await _certificateProbe(site, cancellationToken).ConfigureAwait(false);
            }
            catch (TlsProbeException e)
            {
                return Fail(e.Message);
            }

            if (!string.Equals(info.Fingerprint, request.CertificateFingerprint.Trim(), StringComparison.OrdinalIgnoreCase))
                return Fail("The server now shows another certificate than the one you saw. Read it again before trusting it.");
            if (info.CannotTrust is { } cannot) return Fail(cannot);
            added = new SiteCertificateRecord { Fingerprint = info.Fingerprint, Pem = info.Pem, Subject = info.Subject, NotAfter = info.NotAfter };
        }

        SiteRecord saved;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Read again under the gate: the site may have been edited while the certificate was read.
            if (await _sites.GetAsync(site.Id, cancellationToken).ConfigureAwait(false) is not { } current)
                return Fail("That site no longer exists.");
            if (current.Host != site.Host || current.Port != site.Port || current.Tls != site.Tls)
                return Fail("The site's server changed while its certificate was read. Test it again.");
            List<SiteCertificateRecord> list = added is null ? [] :
            [
                // The newest first; an older one the site trusted for this server is kept (a renewal), up to the limit.
                added,
                .. current.TrustedCertificates.Where(c => c.Fingerprint != added.Fingerprint).Take(SiteCertificateRecord.MaxPerSite - 1),
            ];
            saved = current with { TrustedCertificates = list, ModifiedAt = DateTimeOffset.UtcNow };
            try
            {
                await _sites.SaveAsync(saved, cancellationToken).ConfigureAwait(false);
            }
            catch (SiteStoreException e)
            {
                return Fail(e.Message);
            }
        }
        finally
        {
            _gate.Release();
        }

        ForgetListings(saved.Id);
        var now = await TrustedCertificatesChangedAsync(cancellationToken).ConfigureAwait(false);
        await RaiseChangedAsync(cancellationToken).ConfigureAwait(false);
        return new SiteResultDto
        {
            Ok = true,
            Site = ToDto(saved),
            Message = now ? null : "Trusted. It is used once the site transfers that are running now have finished.",
        };
    }

    /// <summary>
    /// Writes the trusted-certificates file again and lets rclone go so the next one reads it. True when the next call uses
    /// the new file; false when rclone waits for its running work first.
    /// </summary>
    private async Task<bool> TrustedCertificatesChangedAsync(CancellationToken cancellationToken)
    {
        await WriteTrustedCertificatesAsync(cancellationToken).ConfigureAwait(false);
        RcloneSupervisor? supervisor;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            supervisor = _supervisor;
        }
        finally
        {
            _gate.Release();
        }

        return supervisor is null || await supervisor.RenewWhenIdleAsync().ConfigureAwait(false);
    }

    private string TrustedCertificatesPath => Path.Combine(_stateDirectory, "sites", "rclone", RcloneProcess.TrustedCertificatesFileName);

    /// <summary>
    /// The file rclone gets as <c>--ca-cert</c>: the system's roots and every certificate a site trusts. With none trusted
    /// there is no file, and rclone uses the system's own check as it always did.
    /// </summary>
    private async Task WriteTrustedCertificatesAsync(CancellationToken cancellationToken)
    {
        await _certificateFileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await WriteTrustedCertificatesLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A starting rclone may hold the file a moment; the next change or start writes it again.
            _log($"The trusted-certificates file could not be written: {e.Message}");
        }
        finally
        {
            _certificateFileGate.Release();
        }
    }

    private async Task WriteTrustedCertificatesLockedAsync(CancellationToken cancellationToken)
    {
        // SITES.11: an unsaved site's trusted certificates count while it is open.
        var trusted = (await _sites.ListAsync(cancellationToken, includeUnsaved: true).ConfigureAwait(false))
            .SelectMany(s => s.TrustedCertificates).Where(c => c.Matches()).DistinctBy(c => c.Fingerprint).ToList();
        var path = TrustedCertificatesPath;
        if (trusted.Count == 0)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _log($"The trusted-certificates file could not be removed: {e.Message}");
            }

            return;
        }

        var text = new StringBuilder();
        text.Append("# Written by Inbound: the system's root certificates and the server certificates trusted in Sites.\n");
        foreach (var pem in SystemRoots()) text.Append(pem).Append('\n');
        foreach (var certificate in trusted) text.Append(certificate.Pem.Trim()).Append('\n');
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // A new file put in place whole: an rclone starting meanwhile reads the old file or the new one, never half of one.
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, text.ToString(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The roots this PC trusts, as PEM: the file replaces them for rclone, so they go in with the user's certificates. On
    /// Windows that is the Root and third-party root stores; a root Windows would fetch only when first needed is not in
    /// them, which the docs note.
    /// </summary>
    private static IEnumerable<string> SystemRoots()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stores = new List<(StoreName, StoreLocation)> { (StoreName.Root, StoreLocation.CurrentUser), (StoreName.Root, StoreLocation.LocalMachine) };
        if (OperatingSystem.IsWindows()) stores.Add((StoreName.AuthRoot, StoreLocation.LocalMachine));
        foreach (var (name, location) in stores)
        {
            List<string> pems = [];
            try
            {
                using var store = new X509Store(name, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
                foreach (var certificate in store.Certificates)
                {
                    using (certificate)
                    {
                        if (certificate.NotAfter.ToUniversalTime() < DateTime.UtcNow || !seen.Add(certificate.Thumbprint)) continue;
                        pems.Add(certificate.ExportCertificatePem());
                    }
                }
            }
            catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or PlatformNotSupportedException)
            {
                // A store that is not there on this system adds nothing.
            }

            foreach (var pem in pems) yield return pem;
        }
    }
}
