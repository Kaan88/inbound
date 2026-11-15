using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace DownloadManager.Sites;

/// <summary>A server's public host key as the first-use prompt shows it and as rclone's <c>host_keys</c> takes it.</summary>
/// <param name="KeyType">The key's own type from its blob, e.g. <c>ssh-ed25519</c>, <c>ecdsa-sha2-nistp256</c>, <c>ssh-rsa</c>.</param>
/// <param name="Blob">The public key blob (RFC 4253 6.6).</param>
public sealed record SshHostKey(string KeyType, byte[] Blob)
{
    /// <summary><c>SHA256:</c> and the unpadded base64 of the blob's SHA-256, as <c>ssh-keygen -l</c> prints it.</summary>
    public string Fingerprint => "SHA256:" + Convert.ToBase64String(SHA256.HashData(Blob)).TrimEnd('=');

    /// <summary>One <c>host_keys</c> entry: <c>&lt;type&gt; &lt;base64 blob&gt;</c>, the known_hosts form without the host.</summary>
    public string HostKeysEntry => KeyType + " " + Convert.ToBase64String(Blob);
}

public sealed class SshProbeException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads an SSH server's host keys for the first-use prompt (remote-sites.md, "SFTP host keys", step 1; SITES.0 point 4):
/// rclone cannot report a key before a login, and its pin flag sends the credentials first. This runs the key exchange
/// only: our version line, a KEXINIT, and an ECDH init with an ephemeral key; the server's reply carries its host key.
/// No user name, password or key is ever sent, and the connection is closed before authentication.
/// <para>The reply's signature is not checked and the exchange is not finished: the key read here is only shown to the
/// user, who compares the fingerprint; every later connection is rclone's, which checks the accepted key properly
/// (<c>host_keys</c>). This is what <c>ssh-keyscan</c> gives, without needing it on the PC.</para>
/// <para>One connection per host-key type: a server picks one type per exchange, so <see cref="ProbeAllAsync"/> asks
/// once for each type the server lists that this probe knows.</para>
/// </summary>
public static class SshHostKeyProbe
{
    /// <summary>The host-key algorithms asked for, most wanted first (ed25519 first, as OpenSSH's own client).</summary>
    public static readonly IReadOnlyList<string> HostKeyAlgorithms =
    [
        "ssh-ed25519",
        "ecdsa-sha2-nistp256",
        "ecdsa-sha2-nistp384",
        "ecdsa-sha2-nistp521",
        "rsa-sha2-512",
        "rsa-sha2-256",
        "ssh-rsa",
    ];

    private const string Curve25519 = "curve25519-sha256";
    private const string Curve25519Libssh = "curve25519-sha256@libssh.org";
    private const string NistP256 = "ecdh-sha2-nistp256";
    private const byte MsgDisconnect = 1, MsgIgnore = 2, MsgUnimplemented = 3, MsgDebug = 4, MsgKexInit = 20, MsgKexEcdhInit = 30, MsgKexEcdhReply = 31;
    private const int MaxPacket = 256 * 1024;
    private static readonly byte[] ClientVersion = Encoding.ASCII.GetBytes("SSH-2.0-InboundHostKeyProbe_1.0\r\n");

    /// <summary>
    /// Every host key the server offers (one connection each), distinct by blob, in <see cref="HostKeyAlgorithms"/> order.
    /// RSA is asked for once even if the server lists several RSA signature names: they share one key.
    /// </summary>
    public static async Task<IReadOnlyList<SshHostKey>> ProbeAllAsync(string host, int port, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var first = await ProbeAsync(host, port, null, timeout, cancellationToken).ConfigureAwait(false);
        var keys = new List<SshHostKey> { first.Key };
        var askedRsa = first.Key.KeyType == "ssh-rsa";
        foreach (var algorithm in HostKeyAlgorithms)
        {
            if (algorithm == first.Algorithm || !first.ServerHostKeyAlgorithms.Contains(algorithm)) continue;
            var isRsa = algorithm is "rsa-sha2-512" or "rsa-sha2-256" or "ssh-rsa";
            if (isRsa && askedRsa) continue;
            askedRsa |= isRsa;
            var next = await ProbeAsync(host, port, algorithm, timeout, cancellationToken).ConfigureAwait(false);
            if (!keys.Any(k => k.Blob.AsSpan().SequenceEqual(next.Key.Blob))) keys.Add(next.Key);
        }

        return keys;
    }

    /// <summary>The result of one exchange: the key, the algorithm it was asked under, and the server's whole list.</summary>
    public sealed record Result(SshHostKey Key, string Algorithm, IReadOnlyList<string> ServerHostKeyAlgorithms, string ServerVersion);

    /// <summary>One exchange. <paramref name="algorithm"/> asks for one host-key type; null takes the server's pick from <see cref="HostKeyAlgorithms"/>.</summary>
    public static async Task<Result> ProbeAsync(string host, int port, string? algorithm = null, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? TimeSpan.FromSeconds(15));
        try
        {
            using var client = new TcpClient { NoDelay = true };
            await client.ConnectAsync(host, port, limit.Token).ConfigureAwait(false);
            await using var stream = client.GetStream();
            return await ExchangeAsync(stream, algorithm, limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new SshProbeException($"The server at {host}:{port} did not answer in time.");
        }
        catch (SocketException e)
        {
            throw new SshProbeException($"Could not connect to {host}:{port}: {e.Message}", e);
        }
        catch (IOException e)
        {
            throw new SshProbeException($"The connection to {host}:{port} was closed: {e.Message}", e);
        }
    }

    /// <summary>The exchange over any stream (tests drive it with a scripted server).</summary>
    internal static async Task<Result> ExchangeAsync(Stream stream, string? algorithm, CancellationToken cancellationToken)
    {
        await stream.WriteAsync(ClientVersion, cancellationToken).ConfigureAwait(false);
        var serverVersion = await ReadVersionAsync(stream, cancellationToken).ConfigureAwait(false);

        var serverKexInit = await ReadPayloadAsync(stream, MsgKexInit, cancellationToken).ConfigureAwait(false);
        var lists = ParseKexInit(serverKexInit);
        var serverKex = lists[0];
        var serverHostKeys = lists[1];

        var wantedHostKeys = algorithm is null ? HostKeyAlgorithms : [algorithm];
        var hostKeyAlgorithm = wantedHostKeys.FirstOrDefault(serverHostKeys.Contains)
            ?? throw new SshProbeException(algorithm is null
                ? $"The server offers no host key type this probe knows (it offers: {string.Join(", ", serverHostKeys)})."
                : $"The server does not offer a {algorithm} host key.");
        string[] ourKex = [Curve25519, Curve25519Libssh, NistP256];
        var kex = ourKex.FirstOrDefault(serverKex.Contains)
            ?? throw new SshProbeException($"The server's key exchange methods are not supported here ({string.Join(", ", serverKex)}).");

        // The cipher, MAC and compression lists are the server's own: this exchange stops before any of them is used, and
        // echoing them guarantees the server finds a match and goes on to send its key.
        var kexInit = new SshWriter();
        kexInit.Byte(MsgKexInit);
        kexInit.Raw(RandomNumberGenerator.GetBytes(16));
        kexInit.NameList(ourKex);
        kexInit.NameList([hostKeyAlgorithm]);
        for (var i = 2; i < 10; i++) kexInit.NameList(lists[i]);
        kexInit.Byte(0); // first_kex_packet_follows
        kexInit.UInt32(0);
        await WritePacketAsync(stream, kexInit.ToArray(), cancellationToken).ConfigureAwait(false);

        var init = new SshWriter();
        init.Byte(MsgKexEcdhInit);
        if (kex == NistP256)
        {
            using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var point = ecdh.ExportParameters(false).Q;
            init.String([0x04, .. point.X!, .. point.Y!]);
        }
        else
        {
            // Any 32 bytes are a valid X25519 public value; the shared secret is never computed.
            init.String(RandomNumberGenerator.GetBytes(32));
        }

        await WritePacketAsync(stream, init.ToArray(), cancellationToken).ConfigureAwait(false);

        var reply = await ReadPayloadAsync(stream, MsgKexEcdhReply, cancellationToken).ConfigureAwait(false);
        var reader = new SshReader(reply, 1);
        var blob = reader.String();
        var keyType = Encoding.ASCII.GetString(new SshReader(blob, 0).String());
        if (keyType.Length == 0) throw new SshProbeException("The server's host key has no type.");
        return new Result(new SshHostKey(keyType, blob), hostKeyAlgorithm, serverHostKeys, serverVersion);
    }

    private static async Task<string> ReadVersionAsync(Stream stream, CancellationToken cancellationToken)
    {
        // RFC 4253 4.2: the server may send other lines first; each is at most 255 bytes with CR LF.
        for (var lines = 0; lines < 50; lines++)
        {
            var line = await ReadLineAsync(stream, cancellationToken).ConfigureAwait(false);
            if (!line.StartsWith("SSH-", StringComparison.Ordinal)) continue;
            if (line.StartsWith("SSH-2.0-", StringComparison.Ordinal) || line.StartsWith("SSH-1.99-", StringComparison.Ordinal)) return line;
            throw new SshProbeException($"The server speaks an SSH version not supported: {Printable(line)}.");
        }

        throw new SshProbeException("The server did not send an SSH version line.");
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken cancellationToken)
    {
        var bytes = new List<byte>(64);
        var one = new byte[1];
        while (bytes.Count < 256)
        {
            if (await stream.ReadAsync(one, cancellationToken).ConfigureAwait(false) == 0)
                throw new SshProbeException("The server closed the connection before it sent its version (is this an SSH server?).");
            if (one[0] == '\n') return Encoding.ASCII.GetString(bytes.ToArray()).TrimEnd('\r');
            bytes.Add(one[0]);
        }

        throw new SshProbeException("The server sent a line too long for SSH (is this an SSH server?).");
    }

    /// <summary>The next packet of type <paramref name="wanted"/>, skipping ignore, debug and unimplemented messages.</summary>
    private static async Task<byte[]> ReadPayloadAsync(Stream stream, byte wanted, CancellationToken cancellationToken)
    {
        for (var packets = 0; packets < 64; packets++)
        {
            var header = new byte[5];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32BigEndian(header);
            var padding = header[4];
            if (length < 5 || length > MaxPacket || padding + 1 >= length)
                throw new SshProbeException("The server sent a malformed packet.");
            var rest = new byte[length - 1];
            await stream.ReadExactlyAsync(rest, cancellationToken).ConfigureAwait(false);
            var payload = rest[..(int)(length - 1 - padding)];
            switch (payload[0])
            {
                case var type when type == wanted:
                    return payload;
                case MsgIgnore or MsgDebug or MsgUnimplemented:
                    continue;
                case MsgDisconnect:
                    var reader = new SshReader(payload, 1);
                    reader.UInt32();
                    throw new SshProbeException($"The server ended the exchange: {Printable(Encoding.UTF8.GetString(reader.String()))}.");
                default:
                    throw new SshProbeException($"The server sent message {payload[0]} where {wanted} was expected.");
            }
        }

        throw new SshProbeException("The server sent too many packets without its key.");
    }

    /// <summary>The ten name-lists of a KEXINIT payload (RFC 4253 7.1).</summary>
    internal static string[][] ParseKexInit(byte[] payload)
    {
        var reader = new SshReader(payload, 1 + 16);
        var lists = new string[10][];
        for (var i = 0; i < 10; i++)
        {
            var text = Encoding.ASCII.GetString(reader.String());
            lists[i] = text.Length == 0 ? [] : text.Split(',');
        }

        return lists;
    }

    private static async Task WritePacketAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        // RFC 4253 6: total length a multiple of 8, at least 4 bytes of padding; no MAC before keys are in use.
        var padding = 8 - (payload.Length + 5) % 8;
        if (padding < 4) padding += 8;
        var packet = new byte[5 + payload.Length + padding];
        BinaryPrimitives.WriteUInt32BigEndian(packet, (uint)(1 + payload.Length + padding));
        packet[4] = (byte)padding;
        payload.CopyTo(packet, 5);
        RandomNumberGenerator.Fill(packet.AsSpan(5 + payload.Length));
        await stream.WriteAsync(packet, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Printable(string text)
    {
        var clean = new string(text.Where(c => !char.IsControl(c)).Take(200).ToArray());
        return clean.Length == 0 ? "(no reason given)" : clean;
    }

    internal sealed class SshWriter
    {
        private readonly MemoryStream _buffer = new();

        public void Byte(byte value) => _buffer.WriteByte(value);

        public void Raw(ReadOnlySpan<byte> bytes) => _buffer.Write(bytes);

        public void UInt32(uint value)
        {
            Span<byte> bytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
            _buffer.Write(bytes);
        }

        public void String(ReadOnlySpan<byte> bytes)
        {
            UInt32((uint)bytes.Length);
            _buffer.Write(bytes);
        }

        public void NameList(IEnumerable<string> names) => String(Encoding.ASCII.GetBytes(string.Join(',', names)));

        public byte[] ToArray() => _buffer.ToArray();
    }

    internal sealed class SshReader(byte[] data, int offset)
    {
        private int _offset = offset;

        public uint UInt32()
        {
            if (_offset + 4 > data.Length) throw new SshProbeException("The server sent a packet that ends too early.");
            var value = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(_offset));
            _offset += 4;
            return value;
        }

        public byte[] String()
        {
            var length = UInt32();
            if (length > data.Length - _offset) throw new SshProbeException("The server sent a packet that ends too early.");
            var value = data.AsSpan(_offset, (int)length).ToArray();
            _offset += (int)length;
            return value;
        }
    }
}
