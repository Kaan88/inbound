namespace DownloadManager.Cli;

/// <summary>
/// The arguments of the commands in <see cref="Program"/> (not <c>torrent</c>, <c>archive</c> or <c>remote</c>, which
/// read their own). A flag that takes a value takes the next argument, so that value is never read as a positional
/// one: <c>dm add --dest D:\dl URL</c> adds URL, and <c>dm site add --password-env VAR site user</c> adds that site.
/// </summary>
internal static class CliArgs
{
    /// <summary>Every flag of these commands that is followed by a value.</summary>
    public static readonly string[] ValueFlags =
    [
        "--dest", "--to", "--name", "--category", "--start", "--priority", "--package", "--package-name", "--conflict",
        "--description", "--referrer", "--user-agent", "--sha256", "--connections", "--queue", "--header",
        "--password", "--password-env", "--days", "--stop", "--once", "--shutdown",
    ];

    /// <summary>The commands whose arguments are read here.</summary>
    public static bool ReadsArgumentsHere(string command) => command is not ("torrent" or "archive" or "remote");

    /// <summary>The arguments after the command that are neither a flag nor a flag's value.</summary>
    public static string[] Positional(string[] args)
    {
        var positional = new List<string>();
        for (var i = 1; i < args.Length; i++)
        {
            if (IsValueFlag(args[i]))
            {
                i++;
            }
            else if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                positional.Add(args[i]);
            }
        }

        return positional.ToArray();
    }

    /// <summary>
    /// The first flag that needs a value and has none: given last, or followed by another flag
    /// (<c>dm add URL --name --wait</c> would otherwise name the file "--wait"). Null when every one has its value.
    /// </summary>
    public static string? MissingValue(string[] args)
    {
        for (var i = 1; i < args.Length; i++)
        {
            if (!IsValueFlag(args[i]))
            {
                continue;
            }

            if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                return args[i];
            }

            i++;
        }

        return null;
    }

    /// <summary>A whole number given with <paramref name="name"/>; an error when it was given and isn't one.</summary>
    public static bool TryInt(string[] args, string name, out int? value, out string? error)
    {
        value = null;
        error = null;
        if (Program.Option(args, name) is not { } text)
        {
            return true;
        }

        if (int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            value = parsed;
            return true;
        }

        error = $"{name} expects a whole number, not \"{text}\".";
        return false;
    }

    private static bool IsValueFlag(string arg) => ValueFlags.Contains(arg, StringComparer.OrdinalIgnoreCase);
}
