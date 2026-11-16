using System.Runtime.InteropServices;

namespace DownloadManager.Runtime;

/// <summary>What became of a finished download when Windows was asked to check it (L3).</summary>
public enum ScanOutcome
{
    /// <summary>Not asked: not Windows, not from the network, or Windows' attachment services could not be used.</summary>
    NotRun,
    /// <summary>Checked and kept; the file now has the internet mark.</summary>
    Passed,
    /// <summary>The antivirus took the file away (deleted or quarantined).</summary>
    Removed,
}

/// <summary>
/// L3: a finished download is handed to Windows the way a browser hands over its own: <c>IAttachmentExecute::Save</c>
/// writes the internet mark (REV.4) and has the registered antivirus (Microsoft Defender or another) check the file. If the
/// antivirus takes the file away, the download fails and is not retried by itself. Only the site's scheme and host are
/// given as the file's source, as <see cref="MarkOfTheWeb"/> does, since a full address can carry a token.
/// </summary>
public static class DownloadScan
{
    /// <summary>How long the check may take before the download is treated as not checked (a stuck antivirus).</summary>
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(2);

    private static readonly Guid ClientGuid = new("5f0a3a9e-1d3b-4c5e-9b8e-2f6a1c7d0e41");

    public static Task<ScanOutcome> RunAsync(string path, string? address, Action<string>? log = null)
    {
        if (!OperatingSystem.IsWindows() || !MarkOfTheWeb.Applies(address) || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return Task.FromResult(ScanOutcome.NotRun);
        var source = Uri.TryCreate(address, UriKind.Absolute, out var uri) ? $"{uri.Scheme}://{uri.IdnHost}/" : null;
        if (source is null) return Task.FromResult(ScanOutcome.NotRun);

        var done = new TaskCompletionSource<ScanOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The attachment services object lives in a single-threaded apartment, as shell objects do.
        var thread = new Thread(() =>
        {
            try { done.TrySetResult(Save(path, source, log)); }
            catch (Exception ex)
            {
                // Audit 2026-11-15: anything at all. This is a thread of its own, so an exception left here (an HRESULT
                // mapped to ArgumentException, say) would end the Host as a download completes.
                log?.Invoke($"Windows could not check the download ({ex.GetType().Name}); it is marked as from the internet only.");
                done.TrySetResult(ScanOutcome.NotRun);
            }
        })
        { IsBackground = true, Name = "Inbound download check" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return WithLimitAsync(done.Task, log);
    }

    private static async Task<ScanOutcome> WithLimitAsync(Task<ScanOutcome> scan, Action<string>? log)
    {
        if (await Task.WhenAny(scan, Task.Delay(Limit)).ConfigureAwait(false) == scan) return await scan.ConfigureAwait(false);
        log?.Invoke($"Windows did not finish checking the download within {Limit.TotalMinutes:0} minutes; it is kept.");
        return ScanOutcome.NotRun;
    }

    private static ScanOutcome Save(string path, string source, Action<string>? log)
    {
        if (!OperatingSystem.IsWindows()) return ScanOutcome.NotRun;
        var service = (IAttachmentExecute)new AttachmentServices();
        try
        {
            var client = ClientGuid;
            service.SetClientGuid(ref client);
            service.SetClientTitle("Inbound Download Manager");
            service.SetLocalPath(path);
            service.SetSource(source);
            var result = service.Save();
            if (!File.Exists(path))
            {
                log?.Invoke($"The antivirus removed a download (0x{result:X8}).");
                return ScanOutcome.Removed;
            }

            if (result < 0)
            {
                // Refused by policy or the check failed, and the file is still there: it keeps the plain mark, as before.
                log?.Invoke($"Windows did not accept the download's check (0x{result:X8}); the file is kept and marked as from the internet.");
                return ScanOutcome.NotRun;
            }

            return ScanOutcome.Passed;
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    [ComImport, Guid("4125dd96-e03a-4103-8f70-e0597d803b9c")]
    private class AttachmentServices;

    [ComImport, Guid("73db1241-1e85-4581-8e4f-a81e1d0f8c57"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAttachmentExecute
    {
        void SetClientTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetClientGuid(ref Guid guid);
        void SetLocalPath([MarshalAs(UnmanagedType.LPWStr)] string localPath);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string fileName);
        void SetSource([MarshalAs(UnmanagedType.LPWStr)] string source);
        void SetReferrer([MarshalAs(UnmanagedType.LPWStr)] string referrer);
        [PreserveSig] int CheckPolicy();
        [PreserveSig] int Prompt(IntPtr window, int prompt, out int action);
        [PreserveSig] int Save();
        [PreserveSig] int Execute(IntPtr window, [MarshalAs(UnmanagedType.LPWStr)] string verb, out IntPtr process);
        [PreserveSig] int SaveWithUI(IntPtr window);
        [PreserveSig] int ClearClientState();
    }
}
