using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DownloadManager.Contracts;

/// <summary>What a server's https certificate is, for the "Trust this certificate?" question at pairing.</summary>
public sealed class ServerCertificateInfo
{
    public string Subject { get; init; } = "";
    public string Issuer { get; init; } = "";
    public DateTimeOffset NotBefore { get; init; }
    public DateTimeOffset NotAfter { get; init; }

    /// <summary>SHA-256 of the certificate, <c>AB:CD:…</c> (the same form as a site's trusted certificate, SITES.6b).</summary>
    public string Fingerprint { get; init; } = "";

    /// <summary>This device trusts it as it is (its chain and name check pass): nothing to ask.</summary>
    public bool TrustedBySystem { get; init; }

    /// <summary>Why trusting it would not help (out of date), or null.</summary>
    public string? CannotTrust { get; init; }
}

/// <summary>The server's certificate was refused: not one this device trusts, and not the one pinned at pairing.</summary>
public sealed class ServerCertificateRefusedException(string name) : RemoteServerException(
    $"{name}'s certificate is not the one trusted for it. If the server has a new certificate, pair with it again.");

/// <summary>
/// Certificate trust for another Inbound over https (owner, 2026-11-17: "pin at pairing"). A certificate is accepted when
/// the system trusts it, or when it is exactly the one the user trusted at pairing (its SHA-256 equals the pin) and is in
/// its validity dates. Nothing else is ever accepted. Plain http has no certificate and is unaffected.
/// </summary>
public static class ServerCertificates
{
    private static readonly ConcurrentDictionary<string, Lazy<(HttpClient Client, HttpMessageInvoker Upgrades)>> Stacks = new(StringComparer.Ordinal);

    public static string Fingerprint(X509Certificate certificate) =>
        string.Join(':', SHA256.HashData(certificate.GetRawCertData()).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    /// <summary>A fingerprint as typed or stored, in the <c>AB:CD:…</c> form; null when it isn't 32 bytes of hex.</summary>
    public static string? NormalizePin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin)) return null;
        var hex = new string(pin.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 64 || pin.Any(c => !Uri.IsHexDigit(c) && c is not (':' or ' ' or '-'))) return null;
        return string.Join(':', Enumerable.Range(0, 32).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
    }

    /// <summary>The decision, for every https connection to a server: system trust, or the exact pinned certificate in its dates.</summary>
    public static bool Accept(SslPolicyErrors errors, X509Certificate? certificate, string? pin, DateTimeOffset now)
    {
        if (errors == SslPolicyErrors.None) return true;
        if (certificate is null || (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
        if (NormalizePin(pin) is not { } wanted) return false;
        var owned = certificate as X509Certificate2 is null;
        var full = certificate as X509Certificate2 ?? X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        try
        {
            if (now < full.NotBefore.ToUniversalTime() || now > full.NotAfter.ToUniversalTime()) return false;
            var seen = System.Text.Encoding.ASCII.GetBytes(Fingerprint(full));
            return CryptographicOperations.FixedTimeEquals(seen, System.Text.Encoding.ASCII.GetBytes(wanted));
        }
        finally
        {
            if (owned) full.Dispose();
        }
    }

    /// <summary>
    /// The HTTP stack for calls to a server with this pin (or none): no redirects, no proxy, no cookies, as before, and the
    /// certificate decision of <see cref="Accept"/>. One per pin, kept for the process.
    /// </summary>
    internal static HttpClient Client(string? pin) => Stack(pin).Client;

    /// <summary>The stack for WebSocket upgrades to a server with this pin (or none).</summary>
    internal static HttpMessageInvoker Upgrades(string? pin) => Stack(pin).Upgrades;

    private static (HttpClient Client, HttpMessageInvoker Upgrades) Stack(string? pin)
    {
        var key = NormalizePin(pin) ?? "";
        return Stacks.GetOrAdd(key, k => new Lazy<(HttpClient, HttpMessageInvoker)>(() =>
        {
            var pinned = k.Length == 0 ? null : k;
            var client = new HttpClient(Handler(pinned, TimeSpan.FromSeconds(30))) { Timeout = Timeout.InfiniteTimeSpan };
            return (client, new HttpMessageInvoker(Handler(pinned, null)));
        })).Value;
    }

    private static SocketsHttpHandler Handler(string? pin, TimeSpan? idle)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
        };
        if (idle is { } wait) handler.PooledConnectionIdleTimeout = wait;
        // Without a pin the platform's own check stays in charge (no callback at all).
        if (pin is not null)
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = (_, certificate, _, errors) => Accept(errors, certificate, pin, DateTimeOffset.UtcNow),
            };
        return handler;
    }

    /// <summary>Whether an exception from a call is a refused certificate (the TLS handshake's own failure).</summary>
    public static bool IsCertificateFailure(Exception? error)
    {
        for (var e = error; e is not null; e = e.InnerException)
            if (e is AuthenticationException) return true;
        return false;
    }

    /// <summary>
    /// Reads the certificate an https server shows, with a TLS handshake that sends nothing else (no request, no token, no
    /// password). Used at pairing to ask the user; it never makes the certificate trusted.
    /// </summary>
    public static async Task<ServerCertificateInfo> ProbeAsync(Uri address, CancellationToken cancellationToken = default)
    {
        if (address.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("Only an https address has a certificate.", nameof(address));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        X509Certificate2? seen = null;
        var errors = SslPolicyErrors.None;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(address.IdnHost.Trim('[', ']'), address.Port, deadline.Token).ConfigureAwait(false);
            await using var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            var options = new SslClientAuthenticationOptions
            {
                TargetHost = address.IdnHost.Trim('[', ']'),
                RemoteCertificateValidationCallback = (_, certificate, _, policy) =>
                {
                    if (certificate is not null) seen = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    errors = policy;
                    return policy == SslPolicyErrors.None; // the probe trusts nothing the system doesn't
                },
            };
            try { await tls.AuthenticateAsClientAsync(options, deadline.Token).ConfigureAwait(false); }
            catch (AuthenticationException) when (seen is not null) { /* read it; refusing it was the point */ }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new RemoteServerException($"The server at {address} didn't answer in time.");
        }
        catch (Exception e) when (e is SocketException or IOException or AuthenticationException)
        {
            throw new RemoteServerException($"Couldn't open an encrypted connection to {address}. Check the address, and that it starts with https:// only when the server uses https.");
        }

        if (seen is null) throw new RemoteServerException($"The server at {address} showed no certificate.");
        using (seen)
        {
            var now = DateTimeOffset.UtcNow;
            string? cannot = null;
            if (now > seen.NotAfter.ToUniversalTime())
                cannot = $"The server's certificate expired on {seen.NotAfter.ToUniversalTime():d MMMM yyyy}. Trusting it would not help: the server needs a new one.";
            else if (now < seen.NotBefore.ToUniversalTime())
                cannot = $"The server's certificate is not valid until {seen.NotBefore.ToUniversalTime():d MMMM yyyy}. Check this device's clock, or give the server a new certificate.";
            return new ServerCertificateInfo
            {
                Subject = seen.Subject,
                Issuer = seen.Issuer,
                NotBefore = seen.NotBefore.ToUniversalTime(),
                NotAfter = seen.NotAfter.ToUniversalTime(),
                Fingerprint = Fingerprint(seen),
                TrustedBySystem = errors == SslPolicyErrors.None,
                CannotTrust = cannot,
            };
        }
    }
}
