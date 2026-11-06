using System.Text.Json;
using DownloadManager.Contracts;

namespace DownloadManager.Cli;

/// <summary><c>dm archive ...</c>. A password only ever comes from stdin, so it stays out of argv and shell history.</summary>
internal static class ArchiveCommands
{
    private const string Usage = """
        usage: archive status | list
               archive extract <path> | --job ID [--job ID ...] | --package ID  [--password-stdin]
               archive password <set-id> | --package ID | --file PATH   (the password comes from stdin; empty clears it)
               archive retry | cancel | remove <set-id>
               archive settings [--auto-extract on|off] [--unrar-path PATH | --unrar-path-default]
                                [--destination next|subfolder|folder] [--folder PATH]
                                [--preserve-folders on|off] [--conflict rename|overwrite|skip]
                                [--archives keep|delete]
        """;

    private static readonly JsonSerializerOptions PrettyJson = new(IpcJson.Options) { WriteIndented = true };

    private static readonly string[] ValueOptions =
    [
        "--job", "--package", "--file", "--unrar-path", "--destination", "--folder", "--preserve-folders", "--conflict", "--archives", "--auto-extract",
    ];

    public static async Task<int> RunAsync(CliClient client, string[] args, bool json)
    {
        // Refused rather than ignored, so a mistyped flag cannot read as a request without a password.
        if (args.Any(arg => arg.StartsWith("--password", StringComparison.OrdinalIgnoreCase) && !arg.Equals("--password-stdin", StringComparison.OrdinalIgnoreCase)))
        {
            Console.Error.WriteLine("A password is read from stdin only. Use --password-stdin.");
            return 2;
        }

        var positional = Positional(args);
        switch (positional.FirstOrDefault()?.ToLowerInvariant())
        {
            case "status":
                return await StatusAsync(client, json).ConfigureAwait(false);
            case "list":
                return await ListAsync(client, json).ConfigureAwait(false);
            case "extract":
                return await ExtractAsync(client, args, positional, json).ConfigureAwait(false);
            case "password":
                return await PasswordAsync(client, args, positional, json).ConfigureAwait(false);
            case "retry":
                return await ActionAsync(client, ArchiveMessageTypes.Retry, positional, json).ConfigureAwait(false);
            case "cancel":
                return await ActionAsync(client, ArchiveMessageTypes.Cancel, positional, json).ConfigureAwait(false);
            case "remove":
                return await ActionAsync(client, ArchiveMessageTypes.Remove, positional, json).ConfigureAwait(false);
            case "settings":
                return await SettingsAsync(client, args, json).ConfigureAwait(false);
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }

    private static async Task<int> StatusAsync(CliClient client, bool json)
    {
        var settings = (await client.RequestAsync(ArchiveMessageTypes.SettingsGet, new { }).ConfigureAwait(false)).GetPayload<ArchiveSettingsResult>();
        var sets = (await client.RequestAsync(ArchiveMessageTypes.List, new { }).ConfigureAwait(false)).GetPayload<ArchiveListResult>().Sets;
        if (json)
        {
            Print(new { settings.Tool, settings.Settings, Sets = sets });
            return 0;
        }

        PrintSettings(settings);
        Console.WriteLine($"sets: {sets.Count}");
        foreach (var state in sets.GroupBy(set => set.State).OrderBy(group => group.Key))
        {
            Console.WriteLine($"  {state.Key}: {state.Count()}");
        }

        return 0;
    }

    private static async Task<int> ListAsync(CliClient client, bool json)
    {
        var sets = (await client.RequestAsync(ArchiveMessageTypes.List, new { }).ConfigureAwait(false)).GetPayload<ArchiveListResult>().Sets;
        if (json)
        {
            Print(sets);
            return 0;
        }

        if (sets.Count == 0) Console.WriteLine("no archive sets");
        foreach (var set in sets) Console.WriteLine($"{set.Id}  {set.Name}  {set.StateText}");
        return 0;
    }

    private static async Task<int> ExtractAsync(CliClient client, string[] args, IReadOnlyList<string> positional, bool json)
    {
        var request = new ArchiveExtractRequest { FilePath = positional.Count > 1 ? positional[1] : null };
        foreach (var text in OptionValues(args, "--job"))
        {
            if (!Guid.TryParse(text, out var jobId))
            {
                Console.Error.WriteLine($"not a download id: {text}");
                return 2;
            }

            (request.JobIds ??= []).Add(jobId);
        }

        if (Option(args, "--package") is { } packageText)
        {
            if (!Guid.TryParse(packageText, out var packageId))
            {
                Console.Error.WriteLine($"not a package id: {packageText}");
                return 2;
            }

            request.PackageId = packageId;
        }

        if (request.FilePath is null && request.JobIds is null && request.PackageId is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        // Made absolute here, where the current folder means what the user meant by it.
        if (request.FilePath is not null) request.FilePath = Path.GetFullPath(request.FilePath);
        if (HasFlag(args, "--password-stdin")) request.Password = ReadPassword();

        var result = (await client.RequestAsync(ArchiveMessageTypes.Extract, request).ConfigureAwait(false)).GetPayload<ArchiveExtractResult>();
        if (json)
        {
            Print(result);
            return result.Ok ? 0 : 1;
        }

        foreach (var set in result.Sets) Console.WriteLine($"{set.Id}  {set.Name}  {set.StateText}");
        foreach (var ignored in result.Ignored) Console.Error.WriteLine($"{ignored.Item}: {ignored.Reason}");
        if (!result.Ok && result.Ignored.Count == 0) Console.Error.WriteLine(result.Error);
        return result.Ok ? 0 : 1;
    }

    private static async Task<int> PasswordAsync(CliClient client, string[] args, IReadOnlyList<string> positional, bool json)
    {
        var request = new ArchiveSetPasswordRequest();
        if (Option(args, "--package") is { } packageText)
        {
            if (!Guid.TryParse(packageText, out var packageId))
            {
                Console.Error.WriteLine($"not a package id: {packageText}");
                return 2;
            }

            request.PackageId = packageId;
        }
        else if (Option(args, "--file") is { } file)
        {
            request.FilePath = Path.GetFullPath(file);
        }
        else if (positional.Count > 1 && Guid.TryParse(positional[1], out var setId))
        {
            request.SetId = setId;
        }
        else
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        request.Password = ReadPassword();
        return PrintAction((await client.RequestAsync(ArchiveMessageTypes.SetPassword, request).ConfigureAwait(false)).GetPayload<ArchiveActionResult>(), json);
    }

    private static async Task<int> ActionAsync(CliClient client, string type, IReadOnlyList<string> positional, bool json)
    {
        if (positional.Count < 2 || !Guid.TryParse(positional[1], out var setId))
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        return PrintAction((await client.RequestAsync(type, new ArchiveActionRequest { SetId = setId }).ConfigureAwait(false)).GetPayload<ArchiveActionResult>(), json);
    }

    private static async Task<int> SettingsAsync(CliClient client, string[] args, bool json)
    {
        var update = new ArchiveSettingsUpdate
        {
            UnrarPath = Option(args, "--unrar-path"),
            ClearUnrarPath = HasFlag(args, "--unrar-path-default"),
            Folder = Option(args, "--folder"),
            ConflictPolicy = Option(args, "--conflict"),
        };

        if (Option(args, "--destination") is { } destination)
        {
            update.Destination = destination.ToLowerInvariant() is "next" or "nexttoarchive" ? "nextToArchive" : destination;
        }

        if (!TryOnOff(args, "--auto-extract", out var auto) || !TryOnOff(args, "--preserve-folders", out var preserve)) return 2;
        update.AutoExtract = auto;
        update.PreserveFolders = preserve;

        switch (Option(args, "--archives")?.ToLowerInvariant())
        {
            case null:
                break;
            case "keep":
                update.DeleteArchives = false;
                break;
            case "delete":
                update.DeleteArchives = true;
                break;
            default:
                Console.Error.WriteLine("--archives expects keep or delete.");
                return 2;
        }

        var changes = update.UnrarPath is not null || update.ClearUnrarPath || update.Folder is not null || update.ConflictPolicy is not null ||
            update.Destination is not null || update.AutoExtract is not null || update.PreserveFolders is not null || update.DeleteArchives is not null;
        var reply = changes
            ? await client.RequestAsync(ArchiveMessageTypes.SettingsSet, update).ConfigureAwait(false)
            : await client.RequestAsync(ArchiveMessageTypes.SettingsGet, new { }).ConfigureAwait(false);
        var result = reply.GetPayload<ArchiveSettingsResult>();
        if (json)
        {
            Print(result);
        }
        else
        {
            if (!result.Ok) Console.Error.WriteLine(result.Error);
            PrintSettings(result);
        }

        return result.Ok ? 0 : 1;
    }

    private static void PrintSettings(ArchiveSettingsResult result)
    {
        Console.WriteLine($"UnRAR: {result.Tool.Message}");
        Console.WriteLine($"auto-extract: {(result.Settings.AutoExtract ? "on" : "off")}");
        var folder = result.Settings.Destination == "folder" ? $" ({result.Settings.Folder})" : string.Empty;
        Console.WriteLine($"destination: {result.Settings.Destination}{folder}");
        Console.WriteLine($"preserve folders: {(result.Settings.PreserveFolders ? "on" : "off")}");
        Console.WriteLine($"conflict: {result.Settings.ConflictPolicy}");
        Console.WriteLine($"archives after success: {(result.Settings.DeleteArchives ? "delete" : "keep")}");
    }

    private static int PrintAction(ArchiveActionResult result, bool json)
    {
        if (json)
        {
            Print(result);
        }
        else if (!result.Ok)
        {
            Console.Error.WriteLine(result.Error);
        }
        else if (result.Set is { } set)
        {
            Console.WriteLine($"{set.Id}  {set.Name}  {set.StateText}");
        }
        else
        {
            Console.WriteLine("ok");
        }

        return result.Ok ? 0 : 1;
    }

    private static void Print<T>(T value) => Console.WriteLine(JsonSerializer.Serialize(value, PrettyJson));

    /// <summary>One line from stdin, in UTF-8, with its line ending removed and its spaces kept. At a console it is not echoed.</summary>
    private static string ReadPassword()
    {
        return SecretInput.ReadLine("Password: ") ?? string.Empty;
    }

    private static List<string> Positional(string[] args)
    {
        var result = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal))
            {
                result.Add(args[index]);
            }
            else if (ValueOptions.Contains(args[index], StringComparer.OrdinalIgnoreCase))
            {
                index++;
            }
        }

        return result;
    }

    private static string? Option(string[] args, string name) => OptionValues(args, name).FirstOrDefault();

    private static IEnumerable<string> OptionValues(string[] args, string name)
    {
        for (var index = 0; index < args.Length - 1; index++)
        {
            if (args[index].Equals(name, StringComparison.OrdinalIgnoreCase)) yield return args[index + 1];
        }
    }

    private static bool HasFlag(string[] args, string name) => args.Contains(name, StringComparer.OrdinalIgnoreCase);

    private static bool TryOnOff(string[] args, string name, out bool? value)
    {
        value = null;
        switch (Option(args, name)?.ToLowerInvariant())
        {
            case null:
                return true;
            case "on":
                value = true;
                return true;
            case "off":
                value = false;
                return true;
            default:
                Console.Error.WriteLine($"{name} expects on or off.");
                return false;
        }
    }
}
