using System.Globalization;
using System.Net;
using System.Text;

namespace DownloadManager.Core;

public static class FileNameResolver
{
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string Resolve(string? contentDisposition, Uri? finalUri, string? suggestedName = null)
    {
        var candidate = FirstNonEmpty(
            suggestedName,
            FromContentDisposition(contentDisposition),
            finalUri is null ? null : FromUrl(finalUri),
            "download");

        return Sanitize(candidate);
    }

    public static string? FromContentDisposition(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        // Prefer RFC 5987 filename*. Parse the parameters without trusting quotes.
        foreach (var part in SplitParameters(header))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim();
            var value = part[(separator + 1)..].Trim();
            if (key.Equals("filename*", StringComparison.OrdinalIgnoreCase))
            {
                var decoded = DecodeExtendedValue(value);
                if (!string.IsNullOrWhiteSpace(decoded))
                {
                    return decoded;
                }
            }
        }

        foreach (var part in SplitParameters(header))
        {
            var separator = part.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = part[..separator].Trim();
            if (!key.Equals("filename", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return Unquote(part[(separator + 1)..].Trim());
        }

        return null;
    }

    public static string? FromUrl(Uri uri)
    {
        var name = Path.GetFileName(uri.AbsolutePath);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            return Uri.UnescapeDataString(name);
        }
        catch (UriFormatException)
        {
            return name;
        }
    }

    public static string Sanitize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return "download";
        }

        var lastSeparator = name.LastIndexOfAny(new[] { '/', '\\' });
        if (lastSeparator >= 0)
        {
            name = name[(lastSeparator + 1)..];
        }

        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (c < 32 || c == ':' || c == '*' || c == '?' || c == '"' || c == '<' || c == '>' || c == '|')
            {
                builder.Append('_');
            }
            else
            {
                builder.Append(c);
            }
        }

        var cleaned = builder.ToString().Trim().TrimEnd('.');
        if (cleaned.Length == 0)
        {
            cleaned = "download";
        }

        var stem = Path.GetFileNameWithoutExtension(cleaned);
        if (string.IsNullOrWhiteSpace(stem))
        {
            stem = "download";
            cleaned = "download" + Path.GetExtension(cleaned);
        }

        if (ReservedNames.Contains(stem))
        {
            cleaned = "_" + cleaned;
        }

        const int maxLength = 180;
        if (cleaned.Length > maxLength)
        {
            var extension = Path.GetExtension(cleaned);
            if (extension.Length > 20)
            {
                extension = extension[..20];
            }

            var allowedStem = Math.Max(1, maxLength - extension.Length);
            cleaned = cleaned[..Math.Min(allowedStem, cleaned.Length)] + extension;
        }

        return cleaned;
    }

    public static string ResolveUniquePath(string directory, string fileName, ConflictPolicy policy = ConflictPolicy.Rename)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, fileName);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return path;
        }

        return policy switch
        {
            ConflictPolicy.Overwrite => path,
            ConflictPolicy.Fail => throw new IOException($"The destination file already exists: {path}"),
            _ => BuildNumberedPath(directory, fileName),
        };
    }

    private static string BuildNumberedPath(string directory, string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException("Could not find an available filename.");
    }

    private static IEnumerable<string> SplitParameters(string header)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        foreach (var c in header)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                current.Append(c);
            }
            else if (c == ';' && !inQuotes)
            {
                yield return current.ToString();
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }

    private static string? DecodeExtendedValue(string value)
    {
        value = Unquote(value);
        var pieces = value.Split('\'', 3);
        if (pieces.Length != 3)
        {
            return null;
        }

        var charset = pieces[0];
        var encoded = pieces[2];
        try
        {
            var bytes = new List<byte>(encoded.Length);
            for (var i = 0; i < encoded.Length; i++)
            {
                if (encoded[i] == '%' && i + 2 < encoded.Length &&
                    byte.TryParse(encoded.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                {
                    bytes.Add(b);
                    i += 2;
                }
                else
                {
                    bytes.AddRange(Encoding.UTF8.GetBytes(encoded[i].ToString()));
                }
            }

            var encoding = string.IsNullOrWhiteSpace(charset)
                ? Encoding.UTF8
                : Encoding.GetEncoding(charset);
            return encoding.GetString(bytes.ToArray());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or DecoderFallbackException)
        {
            return null;
        }
    }

    private static string Unquote(string value)
    {
        value = value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            value = value[1..^1];
            value = value.Replace("\\\"", "\"", StringComparison.Ordinal);
        }

        return value;
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}

public sealed class SpeedEstimator
{
    private readonly object _gate = new();
    private readonly Queue<Sample> _samples = new();
    private readonly TimeSpan _window;
    private long _lastBytes;
    private double _lastSpeed;
    private TimeSpan? _lastEta;
    private DateTimeOffset _lastSampleAt = DateTimeOffset.MinValue;

    public SpeedEstimator(TimeSpan? window = null)
    {
        _window = window ?? TimeSpan.FromSeconds(4);
    }

    public void Reset(long bytes = 0)
    {
        lock (_gate)
        {
            _samples.Clear();
            _lastBytes = bytes;
            _lastSpeed = 0;
            _lastEta = null;
            _lastSampleAt = DateTimeOffset.MinValue;
        }
    }

    public (double BytesPerSecond, TimeSpan? Eta) Update(long bytes, long? total, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_lastSampleAt == DateTimeOffset.MinValue)
            {
                _lastSampleAt = now;
                _lastBytes = bytes;
                _samples.Enqueue(new Sample(now, bytes));
                return (0, null);
            }

            if (now - _lastSampleAt < TimeSpan.FromMilliseconds(200) && _samples.Count > 1)
            {
                return (_lastSpeed, _lastEta);
            }

            _samples.Enqueue(new Sample(now, bytes));
            while (_samples.Count > 2 && now - _samples.Peek().At > _window)
            {
                _samples.Dequeue();
            }

            var first = _samples.Peek();
            var elapsed = (now - first.At).TotalSeconds;
            if (elapsed <= 0.05)
            {
                return (_lastSpeed, _lastEta);
            }

            var instantaneous = (bytes - first.Bytes) / elapsed;
            _lastSpeed = instantaneous >= 0 ? instantaneous : 0;
            _lastSampleAt = now;
            _lastBytes = bytes;

            if (total is > 0 && _lastSpeed > 1)
            {
                var remaining = Math.Max(0, total.Value - bytes);
                _lastEta = TimeSpan.FromSeconds(remaining / _lastSpeed);
            }
            else
            {
                _lastEta = null;
            }

            return (_lastSpeed, _lastEta);
        }
    }

    private readonly record struct Sample(DateTimeOffset At, long Bytes);
}

public static class RetryPolicy
{
    private const double JitterFraction = 0.2;

    /// <param name="jitterSample">From 0 to 1; random when omitted. Tests pass a fixed value.</param>
    public static TimeSpan Delay(int attempt, EngineOptions options, double? jitterSample = null)
    {
        var exponent = Math.Max(0, attempt - 1);
        var seconds = options.InitialRetryDelay.TotalSeconds * Math.Pow(options.RetryBackoffFactor, exponent);
        seconds = Math.Min(seconds, options.MaxRetryDelay.TotalSeconds);
        // Jitter only shortens, so MaxRetryDelay stays a true bound and delays at the cap still
        // spread out: connections that failed together should not all reconnect together.
        seconds *= 1 - JitterFraction * (jitterSample ?? Random.Shared.NextDouble());
        return TimeSpan.FromSeconds(seconds);
    }

    /// <summary>
    /// The wait before a retry when the server may have asked for one: at least what it asked,
    /// up to <see cref="EngineOptions.MaxRetryAfterDelay"/>, and never less than the normal backoff.
    /// </summary>
    public static TimeSpan Delay(int attempt, EngineOptions options, TimeSpan? retryAfter, double? jitterSample = null)
    {
        var backoff = Delay(attempt, options, jitterSample);
        if (retryAfter is not { } requested || requested <= TimeSpan.Zero)
        {
            return backoff;
        }

        // Added on top here: shortening would come back before the server's stated time.
        var jittered = requested * (1 + JitterFraction * (jitterSample ?? Random.Shared.NextDouble()));
        var bounded = jittered < options.MaxRetryAfterDelay ? jittered : options.MaxRetryAfterDelay;
        return bounded > backoff ? bounded : backoff;
    }
}

public static class Hashing
{
    public static async Task<string> Sha256FileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>For comparing with a checksum a provider supplies. Not a security check.</summary>
    public static async Task<string> Md5FileAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await System.Security.Cryptography.MD5.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

public static class ClipboardUrlExtractor
{
    private static readonly System.Text.RegularExpressions.Regex UrlPattern = new(
        @"https?://[^\s<>""']+",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Returns the first valid absolute HTTP(S) URL in clipboard text.</summary>
    public static string? FirstHttpUrl(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        foreach (System.Text.RegularExpressions.Match match in UrlPattern.Matches(text))
        {
            var candidate = TrimTrailingPunctuation(match.Value);
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https")
            {
                return uri.AbsoluteUri;
            }
        }

        return null;
    }

    private static string TrimTrailingPunctuation(string value)
    {
        var end = value.Length;
        while (end > 0)
        {
            var ch = value[end - 1];
            if (ch is '.' or ',' or ';' or ':' or '!' or '?' or ')' or ']' or '}' or '>' or '"' or '\'')
            {
                end--;
                continue;
            }

            break;
        }

        return value[..end];
    }
}

public static class ShutdownCommand
{
    public static string BuildArguments(string? mode, bool force)
    {
        var command = mode?.Trim().ToLowerInvariant() switch
        {
            "restart" => "/r",
            "logoff" => "/l",
            _ => "/s",
        };
        return $"{command} /t 30" + (force ? " /f" : string.Empty);
    }
}

public static class FormatUtil
{
    public static string Bytes(long bytes)
    {
        string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
        double value = bytes;
        var index = 0;
        while (value >= 1024 && index < suffixes.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return index == 0
            ? $"{bytes} {suffixes[index]}"
            : $"{value:0.0} {suffixes[index]}";
    }

    public static string Speed(double bytesPerSecond)
        => bytesPerSecond <= 0 ? "-" : $"{Bytes((long)bytesPerSecond)}/s";

    public static string Duration(TimeSpan? value)
    {
        if (value is null || value <= TimeSpan.Zero)
        {
            return "-";
        }

        var span = value.Value;
        if (span.TotalHours >= 1)
        {
            return $"{(int)span.TotalHours}h {span.Minutes}m";
        }

        if (span.TotalMinutes >= 1)
        {
            return $"{span.Minutes}m {span.Seconds}s";
        }

        return $"{Math.Max(1, span.Seconds)}s";
    }
}
