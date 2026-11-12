using DownloadManager.Contracts;

namespace DownloadManager.Cli;

internal static partial class Program
{
    /// <summary>What to do with a link that goes through the provider: the CLI has no provider commands.</summary>
    private const string AddInTheApp = "Add it in the app's window or the web UI instead.";

    /// <summary>
    /// <c>dm add</c> means a direct request. With a provider account set up, a hoster link never gets
    /// one, so it is refused with where to add it instead, and <c>--direct</c> doesn't change that. It only answers the
    /// question when nobody can tell what a link is. A Host that can't answer has nothing to say about
    /// it, and ordinary downloads go on as ever. The same goes for each address of a batch or an import.
    /// </summary>
    private sealed class DirectAddCheck(CliClient client, string[] args)
    {
        private readonly bool _direct = CliArgs.HasFlag(args, "--direct");
        private bool _saidIncomplete;

        /// <returns>Why this address can't be added directly. Null when it can.</returns>
        public async Task<string?> WhyNotAsync(string url)
        {
            // UI.B27: no provider can fetch from this PC or its local network, so such an address isn't checked with one.
            if (DownloadManager.Core.NetworkAddresses.IsLocalWebAddress(url)) return null;
            RemoteClassifyResult result;
            try
            {
                result = await client.AskAsync<RemoteClassifyResult>(MessageTypes.RemoteClassify, new RemoteClassifyRequest { Text = url }).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not HostUnavailableException)
            {
                return null;
            }

            switch (result.Kind)
            {
                case "hosterLink":
                    return $"{CliText.Clean(result.HosterName ?? "This site")} goes through {CliText.Clean(result.ProviderName ?? "your provider")}, not by a direct request. {AddInTheApp}";
                case "folderLink":
                    return $"This link is to an item on your {CliText.Clean(result.ProviderName ?? "provider")} account, not a direct download. {AddInTheApp}";
                case "undetermined" when !_direct:
                    return $"{CliText.Clean(result.Reason ?? "It couldn't be checked whether this link goes through your provider.")} To request it directly anyway, add --direct.";
            }

            // Once per run: the same list is behind every address, and a script shouldn't stop for it.
            if (result.CheckIncomplete && !_direct && !_saidIncomplete)
            {
                _saidIncomplete = true;
                Console.Error.WriteLine(CliText.Clean(result.Note ?? "A listed site couldn't be checked against this link."));
            }

            return null;
        }
    }
}
