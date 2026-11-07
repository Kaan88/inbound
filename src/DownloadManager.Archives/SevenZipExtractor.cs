using System.Diagnostics;
using System.Text.RegularExpressions;
using DownloadManager.Core.Archives;

namespace DownloadManager.Archives;

public sealed record SevenZipExtractorOptions
{
    /// <summary>Null uses the 7-Zip bundled with the app, in <c>tools\7-Zip</c> next to it.</summary>
    public string? ToolPath { get; init; }

    public ExtractionLimits Limits { get; init; } = new();

    /// <summary>How long 7-Zip may stay silent. One percent of a huge file can take minutes, so this is generous.</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>Null asks the volume. Returning null skips the check.</summary>
    public Func<string, long?>? AvailableBytes { get; init; }

    internal Action<Process>? ToolStarted { get; init; }

    /// <summary>Stands in for flushing a path to the disk. Tests use it to make a flush fail.</summary>
    internal Action<string>? FlushOverride { get; init; }
}

/// <summary>Where the bundled 7-Zip lives.</summary>
public static class SevenZipTool
{
    public static string BundledPath => Path.Combine(AppContext.BaseDirectory, "tools", "7-Zip", "7z.exe");

    /// <summary>The program to run, or null when it or its 7z.dll is missing.</summary>
    public static string? Find(string? toolPath = null)
    {
        var path = Path.GetFullPath(toolPath ?? BundledPath);
        return File.Exists(path) && File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "7z.dll")) ? path : null;
    }
}

/// <summary>
/// Extraction through the bundled 7-Zip: RAR4 and RAR5, 7z, zip and the other formats 7-Zip reads. Never prints or
/// stores a password. The switches and messages relied on below were checked on 7-Zip 26.04.
/// </summary>
public sealed partial class SevenZipExtractor(SevenZipExtractorOptions? options = null) : IArchiveExtractor
{
    private readonly SevenZipExtractorOptions _options = options ?? new SevenZipExtractorOptions();

    public async Task<ArchiveProbe> ProbeAsync(string firstVolume, string? password, CancellationToken cancellationToken)
    {
        try
        {
            var secret = ToolPassword.From(password);
            var tool = FindTool();
            return await ProbeCoreAsync(tool, Path.GetFullPath(firstVolume), secret, cancellationToken);
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
        StagingArea? staging = null;

        try
        {
            var secret = ToolPassword.From(request.Password);
            cancellationToken.ThrowIfCancellationRequested();
            var tool = FindTool();

            progress?.Report(new ExtractionProgress(ExtractionPhase.Probing));
            var probe = await ProbeCoreAsync(tool, firstVolume, secret, cancellationToken);
            RequireReady(probe, firstVolume, secret);
            ExtractionSafety.ValidateListing(probe, limits);
            var available = (_options.AvailableBytes ?? ExtractionSafety.AvailableBytes)(destination);
            ExtractionSafety.CheckFreeSpace(probe.TotalListedSize, available, limits);

            staging = await StagingArea.PrepareAsync(request.StagingRoot, firstVolume, destination, cancellationToken);

            progress?.Report(new ExtractionProgress(ExtractionPhase.Extracting, 0));
            if (IsCompressedStream(probe.Version))
            {
                probe = await UnwrapStreamAsync(tool, firstVolume, probe, staging, destination, progress, cancellationToken);
            }
            else
            {
                var sent = probe.IsEncrypted ? secret : ToolPassword.None;
                var run = await RunExtractAsync(tool, firstVolume, staging.PayloadFolder, sent, progress, cancellationToken);
                if (run.ExitCode != 0) throw ExtractFailure(run, sent, probe.Version);
            }

            // 7-Zip prints a percentage about every 200 ms and never a closing 100.
            progress?.Report(new ExtractionProgress(ExtractionPhase.Extracting, 100));

            progress?.Report(new ExtractionProgress(ExtractionPhase.Verifying));
            var content = ExtractionSafety.WalkStaging(staging.PayloadFolder, probe, limits);
            MarkFromZone(firstVolume, staging.PayloadFolder, content);

            // The files first, so no rename can ever put data in the destination that isn't on the disk yet.
            var flushed = !request.MakeDurable || FlushAll(content.Files.Select(file => Path.Combine(staging.PayloadFolder, file)), FlushFile, cancellationToken);

            var files = ExtractionFinalizer.MoveIntoDestination(
                staging, content, destination, request.ConflictPolicy, request.PreserveFolders, progress, cancellationToken, request.KeepFiles);

            if (request.MakeDurable)
            {
                var folders = files.Select(file => Path.GetDirectoryName(file.FinalPath)!).Append(destination).Distinct(StringComparer.OrdinalIgnoreCase);
                flushed &= FlushAll(folders, FlushDirectory, cancellationToken);
            }

            var result = new ExtractionResult(destination, files)
            {
                VolumesRead = probe.VolumesRead.Count > 0 ? probe.VolumesRead : [firstVolume],
                FlushedToDisk = request.MakeDurable && flushed,
            };

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
                    await staging.UndoAsync();
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

    /// <summary>
    /// 7-Zip has no switch that carries the download's mark of the web onto what it extracts, so it is written here,
    /// with the zone and without the addresses. Best effort: a file system without streams has no mark to carry.
    /// </summary>
    private static void MarkFromZone(string archive, string payload, StagedContent content)
    {
        if (DataStreams.ReadZoneId(archive) is not { } zone) return;
        foreach (var file in content.Files)
        {
            try
            {
                DataStreams.WriteZone(Path.Combine(payload, file), zone);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // The file stands without it, as it would on a file system without streams.
            }
        }
    }

    private void FlushFile(string path)
    {
        if (_options.FlushOverride is { } flush) flush(path);
        else ExtractionDurability.FlushFile(path);
    }

    private void FlushDirectory(string path)
    {
        if (_options.FlushOverride is { } flush) flush(path);
        else ExtractionDurability.FlushDirectory(path);
    }

    /// <summary>False if any flush failed. The output stands either way, and only deleting the archives depends on this.</summary>
    private static bool FlushAll(IEnumerable<string> paths, Action<string> flush, CancellationToken cancellationToken)
    {
        var all = true;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                flush(path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            {
                all = false;
            }
        }

        return all;
    }

    private string FindTool() =>
        SevenZipTool.Find(_options.ToolPath)
        ?? throw new ExtractionException(ExtractionFailureKind.ToolUnavailable, "The bundled 7-Zip is missing. Repair or reinstall Inbound.");

    private static void RequireReady(ArchiveProbe probe, string firstVolume, ToolPassword secret)
    {
        // Started from a later volume, a tool may go back to the first or extract only the later files, so the
        // name and the header have to agree this is the first.
        if (probe.VolumeNumber > 1 || IsLaterByName(probe, firstVolume))
        {
            throw new ExtractionException(ExtractionFailureKind.MissingVolume, $"{Path.GetFileName(firstVolume)} is not the first volume of its set.", probe.MissingVolumes);
        }

        if (probe.AllVolumesPresent == false)
        {
            throw new ExtractionException(ExtractionFailureKind.MissingVolume, MissingMessage(probe.MissingVolumes), probe.MissingVolumes);
        }

        if (ReferenceEquals(secret, ToolPassword.None) && (probe.HeadersEncrypted || probe.IsEncrypted))
        {
            throw new ExtractionException(ExtractionFailureKind.PasswordRequired, "This archive needs a password.");
        }
    }

    private static bool IsLaterByName(ArchiveProbe probe, string firstVolume)
    {
        if (!probe.IsVolume) return false;
        var set = RarNameSet(firstVolume);
        return set?.FirstVolume is { } first && !first.Equals(Path.GetFileName(firstVolume), StringComparison.OrdinalIgnoreCase);
    }

    private static ArchiveVolumeSet? RarNameSet(string firstVolume)
    {
        var folder = Path.GetDirectoryName(firstVolume);
        if (folder is null || !Directory.Exists(folder)) return null;
        return ArchiveVolumes.FindSet(
            Directory.EnumerateFiles(folder).Select(file => Path.GetFileName(file)),
            firstVolume,
            name => RarVolumeHeader.Read(Path.Combine(folder, name)) == RarVolumeHeaderKind.NotAVolume);
    }

    private async Task<ArchiveProbe> ProbeCoreAsync(string tool, string firstVolume, ToolPassword secret, CancellationToken cancellationToken)
    {
        if (!File.Exists(firstVolume))
        {
            var name = Path.GetFileName(firstVolume);
            throw new ExtractionException(ExtractionFailureKind.MissingVolume, $"The archive {name} is not there.", [name]);
        }

        var hasPassword = !ReferenceEquals(secret, ToolPassword.None);
        var run = await ListAsync(tool, firstVolume, ToolPassword.None, cancellationToken);
        var listing = SevenZipListing.Parse(run.Output);
        var headersEncrypted = HeadersLocked(run);
        var isRar = listing.Type.StartsWith("Rar", StringComparison.OrdinalIgnoreCase) || firstVolume.EndsWith(".rar", StringComparison.OrdinalIgnoreCase);

        if (headersEncrypted && !hasPassword)
        {
            var nameGaps = isRar ? RarNameSet(firstVolume)?.MissingVolumes ?? [] : [];
            return new ArchiveProbe
            {
                FirstVolume = firstVolume,
                IsEncrypted = true,
                HeadersEncrypted = true,
                Entries = [],
                Version = listing.Type,
                IsVolume = listing.IsVolume || nameGaps.Count > 0 || RarNameSet(firstVolume)?.Members.Count > 1,
                VolumeNumber = listing.VolumeNumber,
                AllVolumesPresent = nameGaps.Count > 0 ? false : null,
                MissingVolumes = nameGaps,
            };
        }

        if (headersEncrypted)
        {
            run = await ListAsync(tool, firstVolume, secret, cancellationToken);
            listing = SevenZipListing.Parse(run.Output);
            if (HeadersLocked(run)) throw new ExtractionException(ExtractionFailureKind.WrongPassword, "The password is wrong.");
        }

        var missing = listing.MissingVolumes.ToList();
        RequireWholeSplit(run, firstVolume);
        RequireListed(run, listing, missing.Count > 0);
        var allPresent = missing.Count == 0;

        var volumesRead = new List<string> { firstVolume };
        if (listing.IsVolume && isRar && RarNameSet(firstVolume) is { } set)
        {
            foreach (var gap in set.MissingVolumes)
            {
                if (!missing.Contains(gap, StringComparer.OrdinalIgnoreCase)) missing.Add(gap);
            }

            if (set.MissingVolumes.Count > 0) allPresent = false;
            var folder = Path.GetDirectoryName(firstVolume)!;
            var count = listing.Volumes ?? set.Members.Count;
            volumesRead = set.Members.Take(Math.Max(count, 1)).Select(member => Path.Combine(folder, member)).ToList();
            if (!volumesRead.Contains(firstVolume, StringComparer.OrdinalIgnoreCase)) volumesRead.Insert(0, firstVolume);
        }
        else if (listing.OuterType == "Split")
        {
            volumesRead = SplitVolumes(firstVolume, listing.Volumes);
        }

        // A compressed stream's listing has no names, or one taken from its header: what it holds is read after unpacking.
        var stream = IsCompressedStream(listing.Type);
        IReadOnlyList<ArchiveEntry> entries = stream ? [] : listing.Entries;
        return new ArchiveProbe
        {
            FirstVolume = firstVolume,
            IsEncrypted = headersEncrypted || entries.Any(entry => entry.IsEncrypted),
            HeadersEncrypted = headersEncrypted,
            Entries = entries,
            Version = listing.Type,
            IsVolume = listing.IsVolume,
            VolumeNumber = listing.VolumeNumber,
            AllVolumesPresent = allPresent,
            MissingVolumes = missing,
            ListingHasUnknownLines = !stream && listing.HasUnknownLines,
            ListingHasRepeatedNames = listing.HasRepeatedNames,
            HoldsDataStreams = listing.HoldsDataStreams,
            VolumesRead = volumesRead,
        };
    }

    /// <summary>name.7z.001, name.7z.002 …, as far as 7-Zip said it opened, and only files that are there.</summary>
    private static List<string> SplitVolumes(string firstVolume, int? opened)
    {
        var read = new List<string> { firstVolume };
        if (SplitNumber().Match(firstVolume) is not { Success: true } match) return read;
        var stem = firstVolume[..match.Index];
        var width = match.Groups["n"].Value.Length;
        for (var number = 2; number <= (opened ?? int.MaxValue); number++)
        {
            var next = $"{stem}.{number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(width, '0')}";
            if (!File.Exists(next)) break;
            read.Add(next);
        }

        return read;
    }

    /// <summary>
    /// The names of a name.7z.001 set cannot say how many parts it has. 7-Zip joins what is there, so a set that ends
    /// early reads as an archive cut short, which here means the next part has not arrived.
    /// </summary>
    private static void RequireWholeSplit(ToolRun run, string firstVolume)
    {
        if (!Has(Text(run), "Unexpected end of archive")) return;
        var read = SplitVolumes(firstVolume, null);
        if (read.Count == 0 || SplitNumber().Match(read[^1]) is not { Success: true } last) return;
        var number = int.Parse(last.Groups["n"].Value, System.Globalization.CultureInfo.InvariantCulture) + 1;
        var next = Path.GetFileName(read[^1][..last.Index]) + "." + number.ToString(System.Globalization.CultureInfo.InvariantCulture).PadLeft(last.Groups["n"].Value.Length, '0');
        throw new ExtractionException(ExtractionFailureKind.MissingVolume, $"The set ends early: {next} is not there yet.", [next]);
    }

    private static bool HeadersLocked(ToolRun run) => Text(run).Contains("Cannot open encrypted archive", StringComparison.OrdinalIgnoreCase);

    /// <summary>Exit 0 alone proves nothing: a listing has to reach its entry section.</summary>
    private static void RequireListed(ToolRun run, SevenZipListing listing, bool volumesMissing)
    {
        if (run.ExitCode == 0 && listing.HasEntrySection && listing.Problems.Count == 0) return;

        // A missing volume, or a later volume opened first, is said better by the checks that follow.
        if (listing.HasEntrySection && (volumesMissing || listing.VolumeNumber > 1)) return;

        var text = Text(run);
        if (text.Contains("Cannot open the file as archive", StringComparison.OrdinalIgnoreCase) || text.Contains("Is not archive", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException(ExtractionFailureKind.Corrupted, "This file is not an archive 7-Zip can open, or it is damaged.");
        }

        if (run.ExitCode is 0 or 1 or 2 && (listing.Problems.Count > 0 || !listing.HasEntrySection))
        {
            throw new ExtractionException(ExtractionFailureKind.Corrupted, "The archive is damaged or cut short.");
        }

        throw MapCommonExit(run);
    }

    private Task<ToolRun> ListAsync(string tool, string firstVolume, ToolPassword secret, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "l", "-slt", "-sccUTF-8", "-bsp0", "-bse2" };
        arguments.Add(firstVolume);

        var perEntry = 1024L;
        var keep = Math.Clamp(_options.Limits.MaxEntries * perEntry, 1L << 20, 256L << 20);
        return ToolProcess.RunAsync(
            new ToolInvocation(tool, arguments)
            {
                StandardInput = secret.CopyStdinBytes(),
                KeepOutputCharacters = keep,
                StallTimeout = _options.StallTimeout,
                Started = _options.ToolStarted,
            },
            cancellationToken);
    }

    /// <summary>gzip, bzip2, xz and the like hold one compressed file and no names or sizes 7-Zip can be sure of.</summary>
    internal static bool IsCompressedStream(string format) =>
        format.ToLowerInvariant() is "gzip" or "bzip2" or "xz" or "zstd" or "lzma" or "lzma86" or "z";

    /// <summary>
    /// A .tar.gz is two layers to 7-Zip. The compressed file is written beside the payload first, then a tar inside
    /// is listed and checked like any archive before its files go into the payload. Anything else that comes out,
    /// such as the log of a log.gz, is the one file extracted.
    /// </summary>
    private async Task<ArchiveProbe> UnwrapStreamAsync(
        string tool, string firstVolume, ArchiveProbe outer, StagingArea staging, string destination,
        IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        var layer = Path.Combine(staging.Folder, "layer");
        Directory.CreateDirectory(layer);
        var tarInside = ArchiveNames.IsCompressedTar(Path.GetFileName(firstVolume));
        var run = await RunExtractAsync(tool, firstVolume, layer, ToolPassword.None, tarInside ? null : progress, cancellationToken);
        if (run.ExitCode != 0) throw ExtractFailure(run, ToolPassword.None, outer.Version);
        cancellationToken.ThrowIfCancellationRequested();

        var written = Directory.GetFileSystemEntries(layer);
        if (written.Length != 1 || !File.Exists(written[0]) || (File.GetAttributes(written[0]) & FileAttributes.ReparsePoint) != 0)
        {
            throw new ExtractionException(ExtractionFailureKind.Corrupted, "The compressed file did not hold the one file it should.");
        }

        var inner = written[0];
        var innerName = Path.GetFileName(inner);
        if (!tarInside)
        {
            File.Move(inner, Path.Combine(staging.PayloadFolder, innerName));
            var single = new ArchiveProbe
            {
                FirstVolume = firstVolume,
                Entries = [new ArchiveEntry(innerName, new FileInfo(Path.Combine(staging.PayloadFolder, innerName)).Length, ArchiveEntryKind.File, false)],
                Version = outer.Version,
                IsEncrypted = false,
                HeadersEncrypted = false,
                IsVolume = false,
                AllVolumesPresent = true,
                VolumesRead = [firstVolume],
            };
            ExtractionSafety.ValidateListing(single, _options.Limits);
            return single;
        }

        var tar = await ProbeCoreAsync(tool, inner, ToolPassword.None, cancellationToken);
        if (!string.Equals(tar.Version, "tar", StringComparison.OrdinalIgnoreCase))
        {
            throw new ExtractionException(ExtractionFailureKind.Corrupted, "The compressed file does not hold a tar archive.");
        }

        ExtractionSafety.ValidateListing(tar, _options.Limits);
        var available = (_options.AvailableBytes ?? ExtractionSafety.AvailableBytes)(destination);
        ExtractionSafety.CheckFreeSpace(tar.TotalListedSize, available, _options.Limits);

        run = await RunExtractAsync(tool, inner, staging.PayloadFolder, ToolPassword.None, progress, cancellationToken);
        if (run.ExitCode != 0) throw ExtractFailure(run, ToolPassword.None, tar.Version);
        File.Delete(inner);

        return tar with { FirstVolume = firstVolume, Version = "tar." + outer.Version.ToLowerInvariant(), VolumesRead = [firstVolume] };
    }

    private Task<ToolRun> RunExtractAsync(string tool, string firstVolume, string outputFolder, ToolPassword secret, IProgress<ExtractionProgress>? progress, CancellationToken cancellationToken)
    {
        var reader = new PercentReader(progress);

        // An always-empty staging folder, so -aoa never overwrites anything of ours. No -snl or -snh: links are
        // not created as links. -sns- keeps NTFS streams in the archive out. Progress comes on stdout alone.
        var arguments = new List<string> { "x", "-y", "-aoa", "-sns-", "-sccUTF-8", "-bso0", "-bse2", "-bsp1" };
        arguments.Add("-o" + outputFolder);
        arguments.Add(firstVolume);

        return ToolProcess.RunAsync(
            new ToolInvocation(tool, arguments)
            {
                StandardInput = secret.CopyStdinBytes(),
                StallTimeout = _options.StallTimeout,
                OnOutput = reader.Read,
                Started = _options.ToolStarted,
            },
            cancellationToken);
    }

    private static ExtractionException ExtractFailure(ToolRun run, ToolPassword sent, string format)
    {
        var passwordWasSent = !ReferenceEquals(sent, ToolPassword.None);
        var text = Text(run);
        if (text.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) || HeadersLocked(run))
        {
            if (!passwordWasSent) return new ExtractionException(ExtractionFailureKind.PasswordRequired, "This archive needs a password.");

            // 7-Zip says "Wrong password : name" when the archive's own check says so, and "Data Error in encrypted
            // file. Wrong password?" when it can't tell. RAR5 checks the password first, so there that second one is
            // damage. 7z, zip and RAR4 have no such check, and either is possible.
            var plainlyWrong = WrongPasswordLine().IsMatch(text);
            var damaged = Has(text, "Data Error") || Has(text, "CRC Failed");
            if (plainlyWrong && !damaged) return new ExtractionException(ExtractionFailureKind.WrongPassword, "The password is wrong.");
            if (format == "Rar5") return new ExtractionException(ExtractionFailureKind.Corrupted, "The archive is damaged.");
            return new ExtractionException(ExtractionFailureKind.WrongPassword, "The password is wrong, or the archive is damaged.");
        }

        if (MissingVolume().Match(text) is { Success: true } named)
        {
            var name = Path.GetFileName(named.Groups["name"].Value.Trim());
            return new ExtractionException(ExtractionFailureKind.MissingVolume, MissingMessage([name]), [name]);
        }

        if (run.ExitCode == ExitCodes.Warning)
        {
            return new ExtractionException(ExtractionFailureKind.Unknown, "7-Zip finished with a warning, so nothing was moved out of staging.");
        }

        return MapCommonExit(run);
    }

    private static ExtractionException MapCommonExit(ToolRun run)
    {
        var text = Text(run);
        if (run.ExitCode == ExitCodes.UserBreak) return new ExtractionException(ExtractionFailureKind.Cancelled, "Extraction was stopped.");
        if (run.ExitCode == ExitCodes.Memory) return new ExtractionException(ExtractionFailureKind.Unknown, "7-Zip ran out of memory.");
        if (Has(text, "not enough space")) return new ExtractionException(ExtractionFailureKind.DiskSpace, "7-Zip could not write its output because the disk is full.");
        if (Has(text, "Unsupported Method") || Has(text, "Unsupported feature"))
            return new ExtractionException(ExtractionFailureKind.UnsupportedVersion, "This archive uses a method 7-Zip can't extract.");
        if (Has(text, "Can not open output file") || Has(text, "Access is denied") || Has(text, "Cannot create"))
            return new ExtractionException(ExtractionFailureKind.Permission, "7-Zip could not create a file. The folder may deny writing, a file may be locked, or a path may be too long.");
        if (Has(text, "Data Error") || Has(text, "CRC Failed") || Has(text, "Headers Error") || Has(text, "Unexpected end") || Has(text, "Cannot open the file as archive"))
            return new ExtractionException(ExtractionFailureKind.Corrupted, "The archive is damaged.");
        return new ExtractionException(ExtractionFailureKind.Unknown, $"7-Zip stopped with exit code {run.ExitCode}.");
    }

    private static bool Has(string text, string what) => text.Contains(what, StringComparison.OrdinalIgnoreCase);

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

    [GeneratedRegex(@"^ERROR: Wrong password : ", RegexOptions.CultureInvariant | RegexOptions.Multiline)]
    private static partial Regex WrongPasswordLine();

    [GeneratedRegex(@"Missing volume\s*:\s*(?<name>[^\r\n]+)", RegexOptions.CultureInvariant)]
    private static partial Regex MissingVolume();

    [GeneratedRegex(@"\.(?<n>\d{3,})$", RegexOptions.CultureInvariant)]
    private static partial Regex SplitNumber();

    [GeneratedRegex(@"(?<percent>\d{1,3})%", RegexOptions.CultureInvariant)]
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
        public const int Warning = 1;
        public const int Memory = 8;
        public const int UserBreak = 255;
    }
}
