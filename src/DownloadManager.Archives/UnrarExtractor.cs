using System.Diagnostics;
using System.Text.RegularExpressions;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives;

public sealed record UnrarExtractorOptions
{
    /// <summary>Tried first. A path that does not exist falls through to the installed locations.</summary>
    public string? ToolPath { get; init; }

    /// <summary>Null for the machine's own. Empty skips looking in Program Files.</summary>
    public IReadOnlyList<string>? ProgramFilesDirectories { get; init; }

    /// <summary>Null for this process's PATH. Empty skips looking on PATH.</summary>
    public string? PathVariable { get; init; }

    public ExtractionLimits Limits { get; init; } = new();

    /// <summary>How long UnRAR may stay silent. One percent of a huge file can take minutes, so this is generous.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Null asks the volume. Returning null skips the check.</summary>
    public Func<string, long?>? AvailableBytes { get; init; }

    internal Action<Process>? ToolStarted { get; init; }
}

/// <summary>
/// Extraction through a user-installed UnRAR. Never prints or stores a password. The exit-code and
/// argument choices follow experiments on UnRAR 7.23, and several are not what the documentation
/// alone suggests; the comments below say which.
/// </summary>
public sealed partial class UnrarExtractor(UnrarExtractorOptions? options = null) : IArchiveExtractor
{
    private readonly UnrarExtractorOptions _options = options ?? new UnrarExtractorOptions();

    public async Task<ArchiveProbe> ProbeAsync(string firstVolume, string? password, CancellationToken cancellationToken)
    {
        var secret = UnrarPassword.From(password);
        try
        {
            var tool = FindTool();
            return (await ProbeCoreAsync(tool, Path.GetFullPath(firstVolume), secret, cancellationToken)).Probe;
        }
        catch (Exception exception) when (Translate(exception, password) is { } translated)
        {
            throw translated;
        }
    }

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        IProgress<ExtractionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var firstVolume = Path.GetFullPath(request.FirstVolume);
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Destination));
        var limits = _options.Limits;
        var secret = UnrarPassword.From(request.Password);
        StagingArea? staging = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tool = FindTool();

            progress?.Report(new ExtractionProgress(ExtractionPhase.Probing));
            var (probe, listing) = await ProbeCoreAsync(tool, firstVolume, secret, cancellationToken);
            RequireReady(probe, firstVolume, secret);
            ExtractionSafety.ValidateListing(probe, limits);
            var available = (_options.AvailableBytes ?? ExtractionSafety.AvailableBytes)(destination);
            ExtractionSafety.CheckFreeSpace(probe.TotalListedSize, available, limits);

            staging = await StagingArea.PrepareAsync(request.StagingRoot, firstVolume, destination, cancellationToken);

            progress?.Report(new ExtractionProgress(ExtractionPhase.Extracting, 0));
            var sent = probe.IsEncrypted ? secret : UnrarPassword.None;
            var run = await RunExtractAsync(tool, firstVolume, staging, sent, progress, cancellationToken);
            if (run.ExitCode != 0) throw ExtractFailure(run, listing, sent);

            progress?.Report(new ExtractionProgress(ExtractionPhase.Verifying));
            var content = ExtractionSafety.WalkStaging(staging.PayloadFolder, probe, limits);

            var files = ExtractionFinalizer.MoveIntoDestination(
                staging, content, destination, request.ConflictPolicy, request.PreserveFolders, progress, cancellationToken, request.KeepFiles);

            var result = new ExtractionResult(destination, files);
            if (request.Committed is { } committed)
            {
                try
                {
                    await committed(result);
                }
                catch
                {
                    // Success could not be recorded, so these files must not stand: the next
                    // attempt would find them and, under Rename, write everything a second time.
                    await StagingArea.RecoverAndRemoveAsync(staging.Folder);
                    staging = null;
                    throw;
                }
            }

            await staging.RemoveAsync();
            staging = null;
            return result;
        }
        catch (Exception exception) when (Translate(exception, request.Password) is { } translated)
        {
            if (staging is not null) await staging.RemoveAsync();
            throw translated;
        }
        catch
        {
            if (staging is not null) await staging.RemoveAsync();
            throw;
        }
    }

    private string FindTool() =>
        UnrarToolLocator.Find(_options.ToolPath, _options.ProgramFilesDirectories, _options.PathVariable)
        ?? throw new ExtractionException(ExtractionFailureKind.ToolUnavailable, "UnRAR was not found. Install WinRAR or set the path to UnRAR.exe.");

    private static void RequireReady(ArchiveProbe probe, string firstVolume, UnrarPassword secret)
    {
        // UnRAR started from a later volume goes back to part 1 if it can, and otherwise quietly
        // extracts only the later files, so the name and header have to agree this is the first.
        var isLaterVolume = probe.VolumeNumber > 1 || IsLaterByName(probe, firstVolume);
        if (isLaterVolume)
        {
            throw new ExtractionException(ExtractionFailureKind.MissingVolume, $"{Path.GetFileName(firstVolume)} is not the first volume of its set.", probe.MissingVolumes);
        }

        if (probe.AllVolumesPresent == false)
        {
            throw new ExtractionException(ExtractionFailureKind.MissingVolume, MissingMessage(probe.MissingVolumes), probe.MissingVolumes);
        }

        if (ReferenceEquals(secret, UnrarPassword.None) && (probe.HeadersEncrypted || probe.IsEncrypted))
        {
            throw new ExtractionException(ExtractionFailureKind.PasswordRequired, "This archive needs a password.");
        }
    }

    private static bool IsLaterByName(ArchiveProbe probe, string firstVolume)
    {
        if (!probe.IsVolume) return false;
        var set = NameSet(firstVolume);
        return set?.FirstVolume is { } first && !first.Equals(Path.GetFileName(firstVolume), StringComparison.OrdinalIgnoreCase);
    }

    private static ArchiveVolumeSet? NameSet(string firstVolume)
    {
        var folder = Path.GetDirectoryName(firstVolume);
        if (folder is null || !Directory.Exists(folder)) return null;
        return ArchiveVolumes.FindSet(Directory.EnumerateFiles(folder).Select(file => Path.GetFileName(file)), firstVolume);
    }

    private async Task<(ArchiveProbe Probe, UnrarListing Listing)> ProbeCoreAsync(string tool, string firstVolume, UnrarPassword secret, CancellationToken cancellationToken)
    {
        if (!File.Exists(firstVolume))
        {
            // UnRAR reports a path that does not exist as exit 10, "no files".
            var name = Path.GetFileName(firstVolume);
            throw new ExtractionException(ExtractionFailureKind.MissingVolume, $"The archive {name} is not there.", [name]);
        }

        // Never -v on this first pass: with an encrypted header and no or a wrong password, lt -v
        // exits 6 and names a volume that does not exist.
        var run = await ListAsync(tool, firstVolume, UnrarPassword.None, allVolumes: false, cancellationToken);
        var listing = UnrarListing.Parse(run.Output);
        var headersEncrypted = listing.HeadersEncrypted || run.ExitCode == ExitCodes.PasswordFailure;

        if (!headersEncrypted) RequireListed(run, listing);

        var hasPassword = !ReferenceEquals(secret, UnrarPassword.None);
        if (headersEncrypted && !hasPassword)
        {
            var nameGaps = listing.IsVolume ? NameSet(firstVolume)?.MissingVolumes ?? [] : [];
            return (new ArchiveProbe
            {
                FirstVolume = firstVolume,
                IsEncrypted = true,
                HeadersEncrypted = true,
                Entries = [],
                Version = listing.Version,
                IsVolume = listing.IsVolume,
                VolumeNumber = listing.VolumeNumber,
                AllVolumesPresent = nameGaps.Count > 0 ? false : null,
                MissingVolumes = nameGaps,
            }, listing);
        }

        if (headersEncrypted)
        {
            run = await ListAsync(tool, firstVolume, secret, allVolumes: false, cancellationToken);
            listing = UnrarListing.Parse(run.Output);
            if (run.ExitCode == ExitCodes.PasswordFailure || (run.ExitCode == ExitCodes.ChecksumError && !listing.IsRar5OrLater))
            {
                // RAR4 cannot tell a wrong password from damage: both are exit 3.
                throw new ExtractionException(ExtractionFailureKind.WrongPassword, listing.IsRar5OrLater ? "The password is wrong." : "The password is wrong, or the archive is damaged.");
            }

            RequireListed(run, listing);
        }

        var useSecret = hasPassword && (headersEncrypted || listing.Entries.Any(entry => entry.IsEncrypted));
        var full = await ListAsync(tool, firstVolume, useSecret ? secret : UnrarPassword.None, allVolumes: true, cancellationToken);
        var fullListing = UnrarListing.Parse(full.Output);

        var missing = new List<string>();
        bool allPresent;
        switch (full.ExitCode)
        {
            case ExitCodes.Success:
                allPresent = true;
                break;
            case ExitCodes.MissingVolume:
                // The non-v pass above passed, so this exit 6 is a real gap.
                allPresent = false;
                if (CannotFindVolume().Match(full.Errors + "\n" + full.Output) is { Success: true } named)
                {
                    missing.Add(Path.GetFileName(named.Groups["name"].Value.Trim()));
                }

                break;
            default:
                RequireListed(full, fullListing);
                allPresent = true;
                break;
        }

        if (listing.IsVolume && NameSet(firstVolume) is { } set)
        {
            foreach (var gap in set.MissingVolumes)
            {
                if (!missing.Contains(gap, StringComparer.OrdinalIgnoreCase)) missing.Add(gap);
            }

            if (set.MissingVolumes.Count > 0) allPresent = false;
        }

        var entries = fullListing.Entries;
        return (new ArchiveProbe
        {
            FirstVolume = firstVolume,
            IsEncrypted = headersEncrypted || entries.Any(entry => entry.IsEncrypted),
            HeadersEncrypted = headersEncrypted,
            Entries = entries,
            Version = listing.Version,
            IsVolume = listing.IsVolume,
            VolumeNumber = listing.VolumeNumber,
            AllVolumesPresent = allPresent,
            MissingVolumes = missing,
            ListingHasUnknownLines = fullListing.HasUnknownLines,
        }, listing);
    }

    /// <summary>Exit 0 alone proves nothing: listing a file that is not an archive also exits 0, with no Details line.</summary>
    private static void RequireListed(ToolRun run, UnrarListing listing)
    {
        switch (run.ExitCode)
        {
            case ExitCodes.Success when listing.HasDetails:
                return;
            case ExitCodes.Success:
                throw new ExtractionException(ExtractionFailureKind.Corrupted, "This file is not a RAR archive, or it is damaged.");
            case ExitCodes.Warning:
            case ExitCodes.ChecksumError:
            case ExitCodes.BadArchive:
                throw new ExtractionException(ExtractionFailureKind.Corrupted, "The archive is damaged or cut short.");
            case ExitCodes.NoFiles:
                throw new ExtractionException(ExtractionFailureKind.Corrupted, "The tool found nothing to read in this file.");
            default:
                throw MapCommonExit(run);
        }
    }

    private Task<ToolRun> ListAsync(string tool, string firstVolume, UnrarPassword secret, bool allVolumes, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "lt" };
        if (allVolumes) arguments.Add("-v");
        arguments.AddRange([secret.SwitchArgument, "-scfr", "-idc", "-cfg-", "-c-", firstVolume]);

        // -c- matters for safety as well as noise: a comment is free text and could imitate entries.
        var perEntry = 512L;
        var keep = Math.Clamp(_options.Limits.MaxEntries * perEntry, 1L << 20, 256L << 20);
        return UnrarProcess.RunAsync(
            new ToolInvocation(tool, arguments)
            {
                StandardInput = secret.CopyStdinBytes(),
                KeepOutputCharacters = keep,
                StallTimeout = _options.StallTimeout,
                Started = _options.ToolStarted,
            },
            cancellationToken);
    }

    private Task<ToolRun> RunExtractAsync(string tool, string firstVolume, StagingArea staging, UnrarPassword secret, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        var reader = new PercentReader(progress);

        // -o+ with an always-empty staging folder avoids the overwrite prompt that hangs on open stdin.
        // -ol- keeps symbolic links from being created. -om keeps the zone id on extracted files
        // without copying the source URL. The trailing separator on the destination is required.
        var arguments = new List<string>
        {
            "x", secret.SwitchArgument, "-y", "-o+", "-ol-", "-om", "-idcn", "-cfg-", "-scfr", "-c-",
            firstVolume, staging.PayloadFolder + Path.DirectorySeparatorChar,
        };

        return UnrarProcess.RunAsync(
            new ToolInvocation(tool, arguments)
            {
                StandardInput = secret.CopyStdinBytes(),
                StallTimeout = _options.StallTimeout,
                OnOutput = reader.Read,
                Started = _options.ToolStarted,
            },
            cancellationToken);
    }

    private static ExtractionException ExtractFailure(ToolRun run, UnrarListing listing, UnrarPassword sent)
    {
        var passwordWasSent = !ReferenceEquals(sent, UnrarPassword.None);
        switch (run.ExitCode)
        {
            case ExitCodes.PasswordFailure:
                return passwordWasSent
                    ? new ExtractionException(ExtractionFailureKind.WrongPassword, "The password is wrong.")
                    : new ExtractionException(ExtractionFailureKind.PasswordRequired, "This archive needs a password.");

            case ExitCodes.ChecksumError when passwordWasSent && !listing.IsRar5OrLater:
                // RAR4 reports a wrong password and damage alike. RAR5 has its own exit 11, so
                // there this code means real damage. Not matched on text, which can be localised.
                return new ExtractionException(ExtractionFailureKind.WrongPassword, "The password is wrong, or the archive is damaged.");

            case ExitCodes.ChecksumError:
            case ExitCodes.BadArchive:
                return new ExtractionException(ExtractionFailureKind.Corrupted, "The archive is damaged.");

            case ExitCodes.Warning:
                return new ExtractionException(ExtractionFailureKind.Unknown, "UnRAR finished with a warning, so nothing was moved out of staging.");

            case ExitCodes.NoFiles:
                return new ExtractionException(ExtractionFailureKind.Unknown, "UnRAR found nothing to extract.");

            default:
                return MapCommonExit(run);
        }
    }

    private static ExtractionException MapCommonExit(ToolRun run)
    {
        var text = Text(run);
        switch (run.ExitCode)
        {
            case ExitCodes.Fatal when text.Contains("Unknown method", StringComparison.OrdinalIgnoreCase) || text.Contains("newer version", StringComparison.OrdinalIgnoreCase):
                return new ExtractionException(ExtractionFailureKind.UnsupportedVersion, "This archive needs a newer version of UnRAR.");
            case ExitCodes.Fatal when text.Contains("corrupt", StringComparison.OrdinalIgnoreCase):
                return new ExtractionException(ExtractionFailureKind.Corrupted, "The archive is damaged.");
            case ExitCodes.WriteError:
                return new ExtractionException(ExtractionFailureKind.DiskSpace, "UnRAR could not write its output, usually because the disk is full.");
            case ExitCodes.MissingVolume:
                var name = CannotFindVolume().Match(text) is { Success: true } named ? Path.GetFileName(named.Groups["name"].Value.Trim()) : null;
                return new ExtractionException(ExtractionFailureKind.MissingVolume, MissingMessage(name is null ? [] : [name]), name is null ? null : [name]);
            case ExitCodes.CreateError:
                // UnRAR's own system text under this code said "cannot find the path" whatever the cause.
                return new ExtractionException(ExtractionFailureKind.Permission, "UnRAR could not create a file. The folder may deny writing, a file may be locked, or a path may be too long.");
            case ExitCodes.UserBreak:
                return new ExtractionException(ExtractionFailureKind.Cancelled, "Extraction was stopped.");
            default:
                return new ExtractionException(ExtractionFailureKind.Unknown, $"UnRAR stopped with exit code {run.ExitCode}.");
        }
    }

    private static string Text(ToolRun run) => run.Errors + "\n" + run.Output;

    private static string MissingMessage(IReadOnlyList<string> missing) =>
        missing.Count == 0 ? "A volume of this archive is missing." : "Missing volume: " + string.Join(", ", missing.Select(ExtractionSafety.SafeForDisplay));

    /// <summary>The one place failures leave this class, so a password cannot get out in any message.</summary>
    private static ExtractionException? Translate(Exception exception, string? password)
    {
        return exception switch
        {
            ExtractionException failure => Scrubbed(failure, password),
            OperationCanceledException => new ExtractionException(ExtractionFailureKind.Cancelled, "Extraction was cancelled."),
            IOException or UnauthorizedAccessException => Scrubbed(ExtractionSafety.MapIoFailure(exception, "extracting"), password),
            _ => null,
        };
    }

    private static ExtractionException Scrubbed(ExtractionException failure, string? password)
    {
        if (string.IsNullOrEmpty(password) || !failure.Message.Contains(password, StringComparison.Ordinal)) return failure;
        return new ExtractionException(failure.Kind, failure.Message.Replace(password, "***", StringComparison.Ordinal), failure.MissingVolumes);
    }

    [GeneratedRegex(@"Cannot find volume (?<name>[^\r\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex CannotFindVolume();

    [GeneratedRegex(@"\x08{4}\s*(?<percent>\d{1,3})%", RegexOptions.CultureInvariant)]
    private static partial Regex PercentMark();

    private sealed class PercentReader(IProgress<ExtractionProgress>? progress)
    {
        private const int CarryCharacters = 16;
        private string _carry = string.Empty;
        private int _last = -1;

        public void Read(string text)
        {
            if (progress is null) return;
            var combined = _carry + text;
            var end = 0;
            foreach (Match match in PercentMark().Matches(combined))
            {
                end = match.Index + match.Length;
                var percent = Math.Min(int.Parse(match.Groups["percent"].Value, System.Globalization.CultureInfo.InvariantCulture), 100);
                if (percent > _last)
                {
                    _last = percent;
                    progress.Report(new ExtractionProgress(ExtractionPhase.Extracting, percent));
                }
            }

            var rest = combined[end..];
            _carry = rest.Length > CarryCharacters ? rest[^CarryCharacters..] : rest;
        }
    }

    private static class ExitCodes
    {
        public const int Success = 0;
        public const int Warning = 1;
        public const int Fatal = 2;
        public const int ChecksumError = 3;
        public const int WriteError = 5;
        public const int MissingVolume = 6;
        public const int CreateError = 9;
        public const int NoFiles = 10;
        public const int PasswordFailure = 11;
        public const int BadArchive = 13;
        public const int UserBreak = 255;
    }
}
