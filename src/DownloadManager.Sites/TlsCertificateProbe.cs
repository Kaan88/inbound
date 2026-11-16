using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DownloadManager.Sites;

/// <summary>What a server's TLS certificate says, read by <see cref="TlsCertificateProbe"/>.</summary>
public sealed record TlsCertificateInfo
{
    public required string Subject { get; init; }
    public required string Issuer { get; init; }
    public required DateTimeOffset NotBefore { get; init; }
    public required DateTimeOffset NotAfter { get; init; }

    /// <summary>SHA-256 of the certificate, as <c>AB:CD:…</c>.</summary>
    public required string Fingerprint { get; init; }

    /// <summary>The certificate in PEM: public, written to the trusted-certificates file when the user trusts it.</summary>
    public required string Pem { get; init; }

    /// <summary>The names it is for (subject alternative names, or the common name when it has none).</summary>
    public required IReadOnlyList<string> Names { get; init; }

    /// <summary>True when this PC already trusts it as it is (nothing to ask).</summary>
    public required bool TrustedBySystem { get; init; }

    /// <summary>
    /// Why trusting it would not help, in plain words, or null: the name does not match (rclone still checks names), or it
    /// is outside its dates. A self-signed or unknown-authority certificate has no such reason; it can be trusted.
    /// </summary>
    public string? CannotTrust { get; init; }

    /// <summary>Whether the certificate signs itself (the usual case on a home server).</summary>
    public required bool SelfSigned { get; init; }
}

public sealed class TlsProbeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// SITES.6b: reads the certificate an FTPS or https server shows, with a TLS handshake of our own and nothing after it: no
/// user name or password is ever sent. For explicit FTPS the plain FTP greeting is read and <c>AUTH TLS</c> sent first.
/// </summary>
public static class TlsCertificateProbe
{
    private const int MaxReplyBytes = 8 * 1024;

    public static async Task<TlsCertificateInfo> ProbeAsync(string host, int port, bool explicitFtp, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        var token = timer.Token;
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, port, token).ConfigureAwait(false);
            var stream = tcp.GetStream();
            if (explicitFtp)
            {
                var greeting = await ReadReplyAsync(stream, token).ConfigureAwait(false);
                if (!greeting.StartsWith("220", StringComparison.Ordinal))
                    throw new TlsProbeException("The server did not answer as an FTP server.");
                await stream.WriteAsync("AUTH TLS\r\n"u8.ToArray(), token).ConfigureAwait(false);
                var auth = await ReadReplyAsync(stream, token).ConfigureAwait(false);
                if (!auth.StartsWith("234", StringComparison.Ordinal))
                    throw new TlsProbeException("The server does not offer encrypted FTP (AUTH TLS). Choose another kind of encryption, or plain FTP if you trust the network.");
            }

            X509Certificate2? seen = null;
            var errors = SslPolicyErrors.None;
            var chainProblems = X509ChainStatusFlags.NoError;
            await using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = host,
                // Only to see the certificate: nothing is sent over this connection, and it is closed right away.
                RemoteCertificateValidationCallback = (_, certificate, chain, policy) =>
                {
                    if (certificate is not null) seen = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                    errors = policy;
                    if (chain is not null)
                        foreach (var status in chain.ChainStatus) chainProblems |= status.Status;
                    return true;
                },
            }, token).ConfigureAwait(false);

            if (seen is null) throw new TlsProbeException("The server showed no certificate.");
            return Describe(seen, host, errors, chainProblems, DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TlsProbeException("The server did not answer in time.");
        }
        catch (ArgumentException e)
        {
            throw new TlsProbeException("That server address can't be used.", e);
        }
        catch (Exception e) when (e is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            throw new TlsProbeException(e is SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData }
                ? "That server name was not found."
                : e is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
                    ? "The server refused the connection. Check the port."
                    : e is System.Security.Authentication.AuthenticationException
                        ? "The server did not start an encrypted connection. Check the kind of encryption and the port."
                        : "The connection to the server failed.", e);
        }
    }

    /// <summary>The info for a certificate seen on <paramref name="host"/> with these errors. Public for tests.</summary>
    public static TlsCertificateInfo Describe(X509Certificate2 certificate, string host, SslPolicyErrors errors,
        X509ChainStatusFlags chainProblems, DateTimeOffset now)
    {
        var names = Names(certificate);
        string? cannot = null;
        if (now < certificate.NotBefore.ToUniversalTime() || now > certificate.NotAfter.ToUniversalTime()
            || (chainProblems & X509ChainStatusFlags.NotTimeValid) != 0)
            cannot = now > certificate.NotAfter.ToUniversalTime()
                ? $"The certificate expired on {certificate.NotAfter.ToUniversalTime():d MMMM yyyy}. Trusting it would not help: the server needs a new one."
                : $"The certificate is not valid until {certificate.NotBefore.ToUniversalTime():d MMMM yyyy}. Check this PC's clock, or ask for a new certificate.";
        else if (!certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Any())
            cannot = "The certificate names no server in the way rclone reads (it has no subject alternative name), so trusting it would not help. The server needs a certificate made with the server's name as a subject alternative name.";
        else if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0)
            cannot = $"The certificate is for {(names.Count == 0 ? "no name" : string.Join(", ", names))}, not {host}. Trusting it would not help, because the name is still checked: use the name the certificate is for.";

        var selfSigned = certificate.SubjectName.RawData.AsSpan().SequenceEqual(certificate.IssuerName.RawData);
        return new TlsCertificateInfo
        {
            Subject = certificate.Subject,
            Issuer = certificate.Issuer,
            NotBefore = certificate.NotBefore.ToUniversalTime(),
            NotAfter = certificate.NotAfter.ToUniversalTime(),
            Fingerprint = Fingerprint(certificate),
            Pem = certificate.ExportCertificatePem(),
            Names = names,
            TrustedBySystem = errors == SslPolicyErrors.None,
            CannotTrust = cannot,
            SelfSigned = selfSigned,
        };
    }

    public static string Fingerprint(X509Certificate2 certificate) =>
        string.Join(':', SHA256.HashData(certificate.RawData).Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));

    private static List<string> Names(X509Certificate2 certificate)
    {
        var names = new List<string>();
        foreach (var extension in certificate.Extensions)
        {
            if (extension is not X509SubjectAlternativeNameExtension san) continue;
            names.AddRange(san.EnumerateDnsNames());
            names.AddRange(san.EnumerateIPAddresses().Select(a => a.ToString()));
        }

        if (names.Count == 0 && certificate.GetNameInfo(X509NameType.SimpleName, false) is { Length: > 0 } common) names.Add(common);
        return names;
    }

    /// <summary>One FTP reply, single or multi-line (<c>123-</c> … <c>123 </c>).</summary>
    private static async Task<string> ReadReplyAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new byte[1];
        var line = new StringBuilder();
        string? code = null;
        while (text.Length < MaxReplyBytes)
        {
            if (await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false) == 0)
                throw new TlsProbeException("The server closed the connection.");
            var c = (char)buffer[0];
            if (c != '\n')
            {
                if (c != '\r') line.Append(c);
                continue;
            }

            var current = line.ToString();
            line.Clear();
            text.Append(current).Append('\n');
            if (current.Length < 3) continue;
            code ??= current[..3];
            // The reply ends with a line of the same code followed by a space (or by nothing, from a terse server).
            if (current.StartsWith(code, StringComparison.Ordinal) && (current.Length == 3 || current[3] == ' ')) return text.ToString();
        }

        throw new TlsProbeException("The server's answer was too long.");
    }
}
