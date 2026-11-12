using System.Globalization;
using DownloadManager.Contracts;
using DownloadManager.Core.Providers;

namespace DownloadManager.Desktop.WinUI;

/// <summary>What a provider account looks like on its options page.</summary>
internal sealed record ProviderAccountView(
    string PageTitle,
    string KeyStatus,
    bool KeyStatusIsProblem,
    string KeyBoxHeader,
    string AccountDetails,
    bool CanValidate,
    bool CanClear);

/// <summary>
/// The words the remote screens show. Provider names come from the Host. Nothing here is a local
/// progress figure, and no address gets through: the Host scrubs what it sends, and this does it again.
/// </summary>
internal static partial class RemoteText
{
    public const string HostNotRunning = "Inbound's background program isn't running, so this could not be done. Open Inbound again to start it.";
    public const string HostBusy = "Inbound's background program is running, but too many windows are connected to it right now. Close a progress window, then try again.";
    public const string HostLost = "The connection to Inbound's background program was lost before it answered. Check the list before trying again, because this may have gone through.";
    public const string HostTooSlow = "Inbound's background program didn't answer in time. Check the list before trying again, because this may have gone through.";
    public const string HostUnreadable = "Inbound's background program sent an answer this window couldn't read.";
    public const string HostIncompatible = "Inbound's background program is a different version from this window. Restart Inbound.";
    public const string HostDoesNotOffer = "Inbound's background program doesn't offer this. Restart Inbound, or update it if this keeps happening.";

    private const string Fallback = "That could not be done.";

    /// <summary>The Host's own words when it sent some, otherwise plain words for the kind of failure.</summary>
    public static string Failure(string? error, string? failureKind = null, string? providerName = null)
    {
        var words = UrlRedactor.HideAddresses(error);
        if (!string.IsNullOrWhiteSpace(words)) return words;

        var provider = string.IsNullOrWhiteSpace(providerName) ? "The provider" : providerName;
        return failureKind switch
        {
            "authentication" => $"{provider} refused the API key. Check it, then save it again.",
            "accountLimit" => $"{provider} says your account doesn't allow this.",
            "accountBusy" => $"{provider} says your account is at its limit for now.",
            "linkPasswordRequired" => "This link needs a password from the site it came from. That isn't an archive password.",
            "rateLimited" => $"{provider} is asking for fewer requests. Try again in a little while.",
            "notFound" => $"{provider} has no such item.",
            "rejected" => $"{provider} refused this.",
            "serviceError" => $"{provider} is having problems. Try again later.",
            "network" => $"{provider} could not be reached. Check your connection.",
            "badResponse" => $"{provider} sent an answer this app can't read.",
            "notCached" => $"This isn't cached on {provider}.",
            "alreadyExists" => $"{provider} says this is already on your account.",
            _ => Fallback,
        };
    }

    public static ProviderAccountView Account(RemoteAccountDto account, DateTimeOffset now, IFormatProvider? culture = null)
    {
        var name = string.IsNullOrWhiteSpace(account.ProviderName) ? account.Provider : account.ProviderName;
        var title = $"{name} account";

        // Checked first, because a key that can't be read is not the same as having no key.
        if (account.KeyUnreadable)
        {
            return new ProviderAccountView(
                title,
                $"A {name} API key is saved, but it can't be read on this Windows account. That happens after the app's data was moved or restored from another account or computer. Enter the key again to replace it, or clear it.",
                KeyStatusIsProblem: true,
                KeyBoxHeader: "Enter the API key again",
                AccountDetails: string.Empty,
                CanValidate: false,
                CanClear: true);
        }

        if (!account.HasKey)
        {
            return new ProviderAccountView(
                title,
                $"No API key is saved. Enter your {name} API key to use your account here. You'll find it in your account settings on the {name} site.",
                KeyStatusIsProblem: false,
                KeyBoxHeader: "API key",
                AccountDetails: string.Empty,
                CanValidate: false,
                CanClear: false);
        }

        return new ProviderAccountView(
            title,
            "An API key is saved. It can't be shown again. Enter a new one to replace it.",
            KeyStatusIsProblem: false,
            KeyBoxHeader: "Replace the API key",
            AccountDetails: Details(account, name, now, culture ?? CultureInfo.CurrentCulture),
            CanValidate: true,
            CanClear: true);
    }

    private static string Details(RemoteAccountDto account, string name, DateTimeOffset now, IFormatProvider culture)
    {
        if (account.CheckedAt is not { } checkedAt)
        {
            return $"Not checked since the key changed or the app started. Select Validate to ask {name}.";
        }

        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(account.Plan)) lines.Add($"Plan: {UrlRedactor.HideAddresses(account.Plan)}");
        if (account.PremiumUntil is { } until)
        {
            lines.Add(until > now
                ? $"Premium until {Day(until, culture)}"
                : $"Premium ended on {Day(until, culture)}");
        }
        else
        {
            lines.Add(account.Premium ? "Premium" : "Not premium");
        }

        if (account.CooldownUntil is { } cooldown && cooldown > now)
        {
            lines.Add($"{name} reports a cooldown until {Moment(cooldown, culture)}");
        }

        lines.Add($"Checked {Moment(checkedAt, culture)}");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>What a successful Validate says.</summary>
    public static string Validated(RemoteAccountDto? account)
    {
        var name = account is null || string.IsNullOrWhiteSpace(account.ProviderName) ? "The provider" : account.ProviderName;
        return $"{name} accepted this key.";
    }

    /// <summary>Says whether the bundled 7-Zip is there, in the Host's words, which are about the Host's copy.</summary>
    public static (string Text, bool IsProblem) ToolStatus(ArchiveToolStatusDto tool)
        => (UrlRedactor.RedactText(tool.Message), !tool.Found);

    private static string Day(DateTimeOffset value, IFormatProvider culture)
        => value.ToLocalTime().ToString("D", culture);

    private static string Moment(DateTimeOffset value, IFormatProvider culture)
        => value.ToLocalTime().ToString("f", culture);
}
