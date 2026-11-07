namespace DownloadManager.Containers;

/// <summary>
/// Reads RSDF and CCF files locally and recognises DLC files. Malformed input produces a result that
/// describes the problem. Nothing here decompresses data or makes a network call, and no output can
/// outgrow the input, so a size limit on the input bounds the work.
/// </summary>
public static class ContainerReader
{
    private const string RsdfExtension = ".rsdf";
    private const string CcfExtension = ".ccf";
    private const string DlcExtension = ".dlc";

    public static ContainerImportResult Read(byte[] bytes, string? name = null, ContainerLimits? limits = null)
    {
        limits ??= ContainerLimits.Default;
        try
        {
            return Dispatch(bytes ?? [], name, limits);
        }
        catch (Exception)
        {
            // Exception text can quote the input, and this library must not carry a URL into a log.
            return Failure(limits, ContainerProblemKind.Malformed, "This container could not be read because its contents are damaged.");
        }
    }

    public static ContainerImportResult Read(string path, ContainerLimits? limits = null)
    {
        limits ??= ContainerLimits.Default;
        var (bytes, problem) = Load(path, limits);
        return bytes is null
            ? Failure(limits, problem!.Kind, problem.Message)
            : Read(bytes, Path.GetFileName(path), limits);
    }

    /// <summary>Runs <see cref="Read(string, ContainerLimits?)"/> off the caller's thread. Cancellation is honoured before and after the read.</summary>
    public static async Task<ContainerImportResult> ReadAsync(string path, ContainerLimits? limits = null, CancellationToken cancellationToken = default)
    {
        var result = await Task.Run(() => Read(path, limits), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static ContainerImportResult Dispatch(byte[] data, string? name, ContainerLimits limits)
    {
        var extension = name is null ? string.Empty : Path.GetExtension(name).ToLowerInvariant();
        var named = extension switch
        {
            RsdfExtension => ContainerFormat.Rsdf,
            CcfExtension => ContainerFormat.Ccf,
            DlcExtension => ContainerFormat.Dlc,
            _ => ContainerFormat.Unknown,
        };

        if (data.Length == 0)
        {
            return Failure(limits, ContainerProblemKind.Empty, "This file is empty.", named);
        }

        if (data.Length > limits.MaxInputBytes)
        {
            return TooLarge(limits, named);
        }

        if (CcfReader.HasCcf30Magic(data))
        {
            return CcfReader.Parse(CcfReader.Decrypt(data), limits);
        }

        var isText = ContainerText.LooksLikeText(data);
        if (isText)
        {
            if (ContainerText.IsBlank(data))
            {
                return Failure(limits, ContainerProblemKind.Empty, "This file holds nothing but blank space.", named);
            }

            // The outer layer of every format is text or ciphertext, so markup is never parsed here. Not parsing
            // it also means a DTD in such a file can do nothing.
            if (ContainerText.StartsWithMarkup(data))
            {
                return Failure(limits, ContainerProblemKind.NotAContainer, "This file is a web page or XML document, not a container. A saved error page is a common cause.");
            }

            if (named != ContainerFormat.Dlc && ContainerText.IsHexText(data))
            {
                return RsdfReader.Read(data, limits);
            }

            if (named != ContainerFormat.Rsdf && DlcSupport.TryRecognise(data, out _))
            {
                return DlcSupport.ReadForImport(data, limits);
            }
        }

        if (named == ContainerFormat.Ccf || !isText)
        {
            var attempt = CcfReader.Decrypt(data);
            if (attempt.Plain is not null || named == ContainerFormat.Ccf)
            {
                return CcfReader.Parse(attempt, limits);
            }
        }

        return named switch
        {
            ContainerFormat.Rsdf => RsdfReader.Read(data, limits),
            ContainerFormat.Dlc => DlcSupport.ReadForImport(data, limits),
            _ => Failure(limits, ContainerProblemKind.NotAContainer, "This file is not a container this app recognises (RSDF, CCF or DLC)."),
        };
    }

    private static (byte[]? Bytes, ContainerProblem? Problem) Load(string path, ContainerLimits limits)
    {
        var unreadable = new ContainerProblem(ContainerProblemKind.FileAccess, "This file could not be opened.");
        try
        {
            // File.Exists is false for device paths such as CON, whose reads could block for ever.
            // The CanSeek check below catches anything else that is not an ordinary file.
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return (null, new ContainerProblem(ContainerProblemKind.FileAccess, "This file does not exist."));
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!stream.CanSeek)
            {
                return (null, unreadable);
            }

            if (stream.Length > limits.MaxInputBytes)
            {
                return (null, TooLargeProblem(limits));
            }

            // Allow for growth after the length was read, so the limit holds on the bytes really read.
            var buffer = new byte[(int)stream.Length + 1];
            var total = 0;
            int read;
            while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            return total > limits.MaxInputBytes
                ? (null, TooLargeProblem(limits))
                : (buffer[..total], null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return (null, unreadable);
        }
    }

    private static ContainerImportResult TooLarge(ContainerLimits limits, ContainerFormat format)
    {
        var problem = TooLargeProblem(limits);
        return Failure(limits, problem.Kind, problem.Message, format);
    }

    private static ContainerProblem TooLargeProblem(ContainerLimits limits) =>
        new(ContainerProblemKind.TooLarge, $"This file is larger than the {limits.MaxInputBytes / 1024 / 1024} MB limit for containers.");

    private static ContainerImportResult Failure(
        ContainerLimits limits,
        ContainerProblemKind kind,
        string message,
        ContainerFormat format = ContainerFormat.Unknown)
    {
        var builder = new ContainerResultBuilder(format, limits);
        builder.AddProblem(kind, message);
        return builder.Build();
    }
}
