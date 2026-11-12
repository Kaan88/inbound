namespace DownloadManager.Cli;

/// <summary>
/// The arguments of the commands in <see cref="Program"/> (not <c>torrent</c>, <c>archive</c> or <c>remote</c>, which
/// read their own). A flag that takes a value takes the next argument, so that value is never read as a positional
/// one: <c>dm add --dest D:\dl URL</c> adds URL, and <c>dm site add --password-env VAR site user</c> adds that site.
/// <c>--flag=value</c> works too, and is the way to give a value that starts with "--" (<c>--description=--draft</c>).
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

    /// <summary>
    /// The flags followed by a value for <paramref name="command"/>: <see cref="ValueFlags"/> for the commands read here,
    /// and the lists <c>torrent</c>, <c>archive</c> and <c>remote</c> read their own arguments with.
    /// </summary>
    public static IReadOnlyList<string> ValueFlagsOf(string command) => command.ToLowerInvariant() switch
    {
        "torrent" => TorrentCommands.ValueFlags,
        "archive" => ArchiveCommands.ValueOptions,
        "remote" => Program.RemoteValueOptions,
        _ => ValueFlags,
    };

    /// <summary>
    /// Whether the bare flag <paramref name="name"/> was given. A flag's value is skipped, so
    /// <c>--description=--wait</c> (after <see cref="Expand(string[])"/>, "--description" "--wait") does not read as
    /// <c>--wait</c>. <c>args[0]</c> is the command, which picks the flags that take a value.
    /// </summary>
    public static bool HasFlag(string[] args, string name)
    {
        var flags = args.Length > 0 ? ValueFlagsOf(args[0]) : ValueFlags;
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            if (IsValueFlag(args[i], flags)) i++;
        }

        return false;
    }

    /// <summary>
    /// The value after each <paramref name="name"/> given where a flag goes, never one found where another flag's
    /// value is (<c>--description=--dest --dest D:\x</c> gives D:\x). <c>args[0]</c> is the command.
    /// </summary>
    public static IEnumerable<string> OptionValues(string[] args, string name)
    {
        var flags = args.Length > 0 ? ValueFlagsOf(args[0]) : ValueFlags;
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                yield return args[i + 1];
                i++;
            }
            else if (IsValueFlag(args[i], flags))
            {
                i++;
            }
        }
    }

    /// <summary>
    /// The first <c>--name=value</c> whose name is not a flag that takes a value for this command, cut to its name so a
    /// value (it may be a password) is never repeated. Read before <see cref="Expand(string[])"/>; a flag's value is skipped.
    /// Null when there is none. Such an argument is refused, so a mistyped flag, or a bare one given a value, is not
    /// dropped without a word.
    /// </summary>
    public static string? UnknownJoined(string[] args, IReadOnlyList<string> flags, int first = 1)
    {
        for (var i = first; i < args.Length; i++)
        {
            if (Joined(args[i], flags) is not null)
            {
                continue;
            }

            if (IsValueFlag(args[i], flags))
            {
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) i++;
                continue;
            }

            var equals = args[i].IndexOf('=', StringComparison.Ordinal);
            if (args[i].StartsWith("--", StringComparison.Ordinal) && equals > 2)
            {
                return args[i][..equals];
            }
        }

        return null;
    }

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
    /// The first flag that needs a value and has none: given last, followed by another flag
    /// (<c>dm add URL --name --wait</c> would otherwise name the file "--wait"), or given as <c>--name=</c> with nothing
    /// after the "=". Null when every one has its value. Read before <see cref="Expand"/>.
    /// </summary>
    public static string? MissingValue(string[] args) => MissingValue(args, ValueFlags, nextMustBeValue: true);

    /// <summary>
    /// <see cref="MissingValue(string[])"/> for <paramref name="flags"/>. Without <paramref name="nextMustBeValue"/> only
    /// <c>--flag=</c> with nothing after it counts (torrent, archive and remote read a bare flag's value themselves).
    /// </summary>
    public static string? MissingValue(string[] args, IReadOnlyList<string> flags, bool nextMustBeValue, int first = 1)
    {
        for (var i = first; i < args.Length; i++)
        {
            if (Joined(args[i], flags) is { } joined)
            {
                if (joined.Value.Length == 0) return args[i];
                continue;
            }

            if (!IsValueFlag(args[i], flags))
            {
                continue;
            }

            if (!nextMustBeValue)
            {
                i++;
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

    /// <summary>
    /// The arguments with each <c>--flag=value</c> of a flag that takes a value split into the flag and its value, as
    /// the rest of the commands read them. Any other argument with an "=" is left as it is.
    /// </summary>
    public static string[] Expand(string[] args) => Expand(args, ValueFlags);

    /// <summary><see cref="Expand(string[])"/> for <paramref name="flags"/>; arguments before <paramref name="first"/> stay as they are.</summary>
    public static string[] Expand(string[] args, IReadOnlyList<string> flags, int first = 1)
    {
        var expanded = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (i >= first && IsValueFlag(args[i], flags) && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                // A flag's value is never split, even one that looks like --flag=value.
                expanded.Add(args[i]);
                expanded.Add(args[++i]);
            }
            else if (i >= first && Joined(args[i], flags) is { } joined)
            {
                expanded.Add(joined.Flag);
                expanded.Add(joined.Value);
            }
            else
            {
                expanded.Add(args[i]);
            }
        }

        return expanded.ToArray();
    }

    /// <summary>
    /// The first <c>--flag=value</c> of a flag in <paramref name="flags"/> whose value starts with "--", cut to the flag.
    /// <c>torrent</c> never reads such a value (a bare flag followed by "--" has none), so it is refused there, not dropped.
    /// </summary>
    public static string? JoinedValueStartingWithDashes(string[] args, IReadOnlyList<string> flags, int first = 1)
    {
        for (var i = first; i < args.Length; i++)
        {
            if (Joined(args[i], flags) is { } joined && joined.Value.StartsWith("--", StringComparison.Ordinal))
            {
                return joined.Flag;
            }
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

    /// <summary><c>--flag=value</c> of a flag that takes a value; null for anything else.</summary>
    private static (string Flag, string Value)? Joined(string arg, IReadOnlyList<string> flags)
    {
        var equals = arg.IndexOf('=', StringComparison.Ordinal);
        return arg.StartsWith("--", StringComparison.Ordinal) && equals > 2 && IsValueFlag(arg[..equals], flags)
            ? (arg[..equals], arg[(equals + 1)..])
            : null;
    }

    private static bool IsValueFlag(string arg) => IsValueFlag(arg, ValueFlags);

    private static bool IsValueFlag(string arg, IReadOnlyList<string> flags) => flags.Contains(arg, StringComparer.OrdinalIgnoreCase);
}
