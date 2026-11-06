using System.Text;

namespace DownloadManager.Cli;

/// <summary>
/// The one way a secret is read from standard input. At a console it isn't shown. Redirected, it is read as UTF-8
/// whatever the console's code page, through a single reader, because a reader takes in more than one line
/// at a time and a second one would find a later line already gone.
/// </summary>
internal static class SecretInput
{
    private static readonly Lazy<StreamReader> Redirected = new(() =>
        new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true));

    /// <summary>One line with its ending removed and its spaces kept. Null at the end of redirected input.</summary>
    public static string? ReadLine(string prompt)
    {
        if (Console.IsInputRedirected)
        {
            return Redirected.Value.ReadLine();
        }

        Console.Error.Write(prompt);
        var typed = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                return typed.ToString();
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (typed.Length > 0) typed.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                typed.Append(key.KeyChar);
            }
        }
    }
}
