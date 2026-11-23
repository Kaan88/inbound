using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DownloadManager.Sites;

/// <summary>
/// SITES.10 (audit 2026-11-17): each https site (WebDAV, S3) that trusts certificates of its own gets its own CA file, given to
/// rclone as that remote's <c>override.ca_cert</c>. A certificate trusted for one site never vouches for another server
/// (one shared <c>--ca-cert</c> did: a trusted CA certificate would have signed for every site). The file holds the
/// system's roots, which it replaces for that remote, and the site's certificates. Its name carries the site and a hash of
/// the certificates, so a new trust is a new remote for rclone (no cached connection keeps the old list) and no restart
/// is needed. FTPS sites keep the Host's one <c>--ca-cert</c> file (<see cref="RcloneProcess.TrustedCertificatesFileName"/>).
/// </summary>
public static class SiteCertificateFiles
{
    private const string Extension = ".pem";
    private static readonly object WriteLock = new();

    /// <summary>The site's CA file, written if it is not there yet, or null when the site trusts no certificate of its own.</summary>
    public static string? PathFor(SiteRecord site, string directory)
    {
        var trusted = Trusted(site);
        if (trusted.Count == 0) return null;
        var path = Path.Combine(directory, FileName(site.Id, trusted));
        if (File.Exists(path)) return path;
        var text = new StringBuilder();
        text.Append("# Written by Inbound: the system's root certificates and the server certificates this remote trusts.\n");
        foreach (var pem in SystemRoots()) text.Append(pem).Append('\n');
        foreach (var certificate in trusted) text.Append(certificate.Pem.Trim()).Append('\n');
        lock (WriteLock)
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(directory);
            // Put in place whole: rclone reads the file or nothing, never half of one.
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                // Another process (a second Host on the same state) put the same file in place first.
            }
            finally
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        return path;
    }

    /// <summary>The system's roots alone, for an https remote of an rclone that has the FTPS sites' <c>--ca-cert</c>.</summary>
    public static string RootsPath(string directory)
    {
        var path = Path.Combine(directory, RootsFileName);
        if (File.Exists(path)) return path;
        var text = new StringBuilder();
        text.Append("# Written by Inbound: the system's root certificates.\n");
        foreach (var pem in SystemRoots()) text.Append(pem).Append('\n');
        lock (WriteLock)
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(directory);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
            }
            finally
            {
                try { File.Delete(temp); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        return path;
    }

    private const string RootsFileName = "system-roots.pem";

    /// <summary>
    /// Removes the files no site uses now (a forgotten certificate, a deleted site). With <paramref name="all"/>, every file
    /// goes, so the next use writes it with today's system roots (at rclone's start).
    /// </summary>
    public static void Prune(string directory, IEnumerable<SiteRecord> sites, bool all, Action<string>? log = null)
    {
        if (!Directory.Exists(directory)) return;
        var keep = all ? [] : sites.Select(s => (s.Id, Trusted: Trusted(s))).Where(s => s.Trusted.Count > 0)
            .Select(s => FileName(s.Id, s.Trusted)).Append(RootsFileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (keep.Contains(Path.GetFileName(file))) continue;
            try
            {
                File.Delete(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"A trusted-certificates file could not be removed: {e.Message}");
            }
        }
    }

    private static List<SiteCertificateRecord> Trusted(SiteRecord site) =>
        site.TrustedCertificates.Where(c => c.Matches()).DistinctBy(c => c.Fingerprint).ToList();

    private static string FileName(string siteId, IEnumerable<SiteCertificateRecord> trusted)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', trusted.Select(c => c.Fingerprint))));
        return siteId + "-" + Convert.ToHexStringLower(hash.AsSpan(0, 8)) + Extension;
    }

    /// <summary>
    /// The roots this PC trusts, as PEM. On Windows that is the Root and third-party root stores; a root Windows would
    /// fetch only when first needed is not in them, which the docs note.
    /// </summary>
    public static IEnumerable<string> SystemRoots()
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
            catch (Exception e) when (e is CryptographicException or PlatformNotSupportedException)
            {
                // A store that is not there on this system adds nothing.
            }

            foreach (var pem in pems) yield return pem;
        }
    }
}
