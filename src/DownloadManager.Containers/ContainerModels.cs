namespace DownloadManager.Containers;

public enum ContainerFormat
{
    Unknown,
    Rsdf,
    Ccf,
    Dlc,
}

public enum ContainerProblemKind
{
    Empty,
    TooLarge,
    TooManyEntries,
    Truncated,
    Malformed,
    NotAContainer,
    DecryptionFailed,
    UnsafeXml,
    InvalidEntry,
    NotSupported,
    FileAccess,
}

/// <summary>Why part or all of a container could not be read. Messages never quote the container's contents.</summary>
public sealed record ContainerProblem(ContainerProblemKind Kind, string Message)
{
    public override string ToString() => $"{Kind}: {Message}";
}

/// <summary>A package as described by the container.</summary>
/// <param name="ArchivePasswordCandidates">
/// Passwords the container offers for this package's archives, to be tried only on that archive set.
/// They do not open the container, and they are not hoster (link) passwords.
/// </param>
public sealed record ContainerPackage(string? Name, IReadOnlyList<string> ArchivePasswordCandidates, string? Comment)
{
    // The generated record ToString would print the passwords into any log line that touches this.
    public override string ToString() =>
        $"Package ({ArchivePasswordCandidates.Count} password candidate(s), comment: {Comment is not null})";
}

/// <summary>A link taken from a container. All values are untrusted input.</summary>
public sealed record ContainerLink(string Url, ContainerPackage? Package, string? FileName, long? SizeBytes)
{
    // The generated record ToString would print the URL into any log line that touches this.
    public override string ToString() => $"Link (in package: {Package is not null}, size known: {SizeBytes is not null})";
}

/// <summary>Bounds on what one container may cost to read.</summary>
public sealed record ContainerLimits
{
    public static ContainerLimits Default { get; } = new();

    public long MaxInputBytes { get; init; } = 16 * 1024 * 1024;
    public int MaxLinks { get; init; } = 100_000;
    public int MaxPackages { get; init; } = 10_000;
    public int MaxFieldChars { get; init; } = 8 * 1024;
    public int MaxXmlDepth { get; init; } = 32;
    public int MaxProblems { get; init; } = 100;
}

public sealed class ContainerImportResult
{
    internal ContainerImportResult(
        ContainerFormat format,
        string? formatVersion,
        IReadOnlyList<ContainerPackage> packages,
        IReadOnlyList<ContainerLink> links,
        IReadOnlyList<ContainerProblem> problems)
    {
        Format = format;
        FormatVersion = formatVersion;
        Packages = packages;
        Links = links;
        Problems = problems;
    }

    public ContainerFormat Format { get; }

    /// <summary>The scheme that opened the file, such as "3.0" for CCF, when one did.</summary>
    public string? FormatVersion { get; }

    public IReadOnlyList<ContainerPackage> Packages { get; }

    public IReadOnlyList<ContainerLink> Links { get; }

    public IReadOnlyList<ContainerProblem> Problems { get; }

    /// <summary>True when at least one link came out. Problems may still describe links that did not.</summary>
    public bool Succeeded => Links.Count > 0;

    public override string ToString() =>
        $"{Format}: {Links.Count} link(s), {Packages.Count} package(s), {Problems.Count} problem(s)";
}

internal sealed class ContainerResultBuilder(ContainerFormat format, ContainerLimits limits)
{
    private readonly List<ContainerPackage> _packages = [];
    private readonly List<ContainerLink> _links = [];
    private readonly List<ContainerProblem> _problems = [];
    private readonly HashSet<string> _seenUrls = new(StringComparer.Ordinal);
    private bool _linkLimitReported;
    private bool _packageLimitReported;
    private bool _problemLimitReported;

    public int LinkCount => _links.Count;

    public bool LinkLimitReached => _links.Count >= limits.MaxLinks;

    public bool HasSeenUrl(string url) => _seenUrls.Contains(url);

    public void AddProblem(ContainerProblemKind kind, string message)
    {
        if (_problems.Count < limits.MaxProblems)
        {
            _problems.Add(new ContainerProblem(kind, message));
        }
        else if (!_problemLimitReported)
        {
            // Every line of a hostile file can be bad, so the list itself needs a bound.
            _problemLimitReported = true;
            _problems.Add(new ContainerProblem(ContainerProblemKind.TooManyEntries, "More problems were found and are not listed."));
        }
    }

    /// <summary>Returns false once the link limit has been reached, so callers can stop.</summary>
    public bool TryAddLink(string url, ContainerPackage? package, string? fileName, long? sizeBytes)
    {
        if (LinkLimitReached)
        {
            ReportLinkLimitExceeded();
            return false;
        }

        _links.Add(new ContainerLink(url, package, fileName, sizeBytes));
        _seenUrls.Add(url);
        return true;
    }

    public void ReportLinkLimitExceeded()
    {
        if (!_linkLimitReported)
        {
            _linkLimitReported = true;
            AddProblem(ContainerProblemKind.TooManyEntries, $"The container holds more than {limits.MaxLinks} links. The rest were ignored.");
        }
    }

    public bool TryAddPackage(ContainerPackage package)
    {
        if (_packages.Count >= limits.MaxPackages)
        {
            if (!_packageLimitReported)
            {
                _packageLimitReported = true;
                AddProblem(ContainerProblemKind.TooManyEntries, $"The container holds more than {limits.MaxPackages} packages. The rest were ignored.");
            }

            return false;
        }

        _packages.Add(package);
        return true;
    }

    public ContainerImportResult Build(string? formatVersion = null) =>
        new(format, formatVersion, _packages.ToArray(), _links.ToArray(), _problems.ToArray());
}
