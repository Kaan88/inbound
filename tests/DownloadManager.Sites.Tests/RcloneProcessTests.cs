using System.Diagnostics;
using System.Text.Json.Nodes;
using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>A real rclone (INBOUND_RCLONE). Each test uses its own state folder and ends what it started.</summary>
public sealed class RcloneProcessTests : IDisposable
{
    private readonly string _state = Path.Combine(Path.GetTempPath(), "inbound-sites-tests", Guid.NewGuid().ToString("N"));
    private readonly List<string> _log = [];

    public void Dispose()
    {
        try { Directory.Delete(_state, recursive: true); }
        catch (IOException) { }
    }

    private void Log(string line)
    {
        lock (_log) _log.Add(line);
    }

    [RcloneFact]
    public async Task Starts_answers_with_its_credentials_and_quits_when_asked()
    {
        await using var rclone = await RcloneProcess.StartAsync(RcloneBinary.Path!, _state, Log);
        var version = await rclone.Client.CallAsync("core/version");
        Assert.StartsWith("v1.", version["version"]!.GetValue<string>());
        Assert.True(File.Exists(Path.Combine(_state, RcloneProcess.PidFileName)));

        // Without the credentials rclone refuses.
        using var anonymous = new HttpClient();
        var refused = await anonymous.PostAsync(new Uri(rclone.Client.BaseAddress, "core/version"), new StringContent("{}"));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, refused.StatusCode);

        Assert.True(await rclone.StopAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, rclone.ExitCode);
        Assert.False(File.Exists(Path.Combine(_state, RcloneProcess.PidFileName)));
    }

    [RcloneFact]
    public async Task The_password_is_not_on_the_command_line_and_no_config_file_is_written()
    {
        if (!OperatingSystem.IsLinux()) return; // /proc only
        await using var rclone = await RcloneProcess.StartAsync(RcloneBinary.Path!, _state, Log);
        var cmdline = (await File.ReadAllTextAsync($"/proc/{rclone.ProcessId}/cmdline")).Replace('\0', ' ');
        Assert.Contains("rcd", cmdline);
        Assert.DoesNotContain("rc-pass", cmdline);
        Assert.DoesNotContain("rc-user", cmdline);
        Assert.Equal("", rclone.Client.Redact(""));

        await rclone.Client.CallAsync("core/version");
        Assert.Empty(Directory.EnumerateFiles(_state, "*.conf", SearchOption.AllDirectories));
    }

    [RcloneFact]
    public async Task Inherited_rclone_variables_are_not_passed_on()
    {
        if (!OperatingSystem.IsLinux()) return;
        Environment.SetEnvironmentVariable("RCLONE_CONFIG", Path.Combine(_state, "should-not-be-used.conf"));
        try
        {
            await using var rclone = await RcloneProcess.StartAsync(RcloneBinary.Path!, _state, Log);
            var environ = await File.ReadAllTextAsync($"/proc/{rclone.ProcessId}/environ");
            var names = environ.Split('\0').Where(e => e.StartsWith("RCLONE_", StringComparison.Ordinal)).Select(e => e[..e.IndexOf('=')]).Order();
            Assert.Equal(["RCLONE_RC_PASS", "RCLONE_RC_USER"], names);
        }
        finally
        {
            Environment.SetEnvironmentVariable("RCLONE_CONFIG", null);
        }
    }

    [RcloneFact]
    public async Task Obscure_registers_both_forms_as_secrets_and_errors_are_redacted()
    {
        await using var rclone = await RcloneProcess.StartAsync(RcloneBinary.Path!, _state, Log);
        const string password = "hunter2-correct-horse";
        var obscured = await rclone.Client.ObscureAsync(password);
        Assert.NotEqual(password, obscured);

        // An SFTP remote with the password to a port nothing listens on: rclone's error repeats its parameters.
        var error = await Assert.ThrowsAsync<RcloneCallException>(() => rclone.Client.CallAsync("operations/list", new JsonObject
        {
            ["fs"] = new JsonObject { ["type"] = "sftp", ["host"] = "127.0.0.1", ["port"] = "1", ["user"] = "nobody", ["pass"] = obscured, ["_root"] = "/" },
            ["remote"] = "",
        }));
        Assert.DoesNotContain(password, error.Message);
        Assert.DoesNotContain(obscured, error.Message);
    }

    [RcloneFact]
    public async Task A_killed_rclone_is_seen_by_the_supervisor_and_started_again()
    {
        var supervisor = new RcloneSupervisor(async ct => await RcloneProcess.StartAsync(RcloneBinary.Path!, _state, Log, null, ct),
            new RcloneSupervisorOptions { FirstRestartDelay = TimeSpan.FromMilliseconds(100) }, Log);
        await using (supervisor)
        {
            int firstPid;
            using (var lease = await supervisor.AcquireAsync())
            {
                firstPid = lease.ProcessId;
                Process.GetProcessById(firstPid).Kill();
                await WaitFor(() => supervisor.Status.State == RcloneState.Waiting);
                await Assert.ThrowsAsync<RcloneUnavailableException>(() => lease.Client.CallAsync("core/version"));
            }

            using var again = await supervisor.AcquireAsync();
            Assert.NotEqual(firstPid, again.ProcessId);
            await again.Client.CallAsync("core/version");
        }

        Assert.Equal(RcloneState.Off, supervisor.Status.State);
    }

    [RcloneFact]
    public async Task Idle_rclone_stops_and_leaves_no_process()
    {
        var supervisor = new RcloneSupervisor(async ct => await RcloneProcess.StartAsync(RcloneBinary.Path!, _state, Log, null, ct),
            new RcloneSupervisorOptions { IdleDelay = TimeSpan.FromMilliseconds(200) }, Log);
        await using (supervisor)
        {
            int pid;
            using (var lease = await supervisor.AcquireAsync()) pid = lease.ProcessId;
            await WaitFor(() => supervisor.Status.State == RcloneState.Off);
            Assert.False(IsRunning(pid));
            Assert.False(File.Exists(Path.Combine(_state, RcloneProcess.PidFileName)));
        }
    }

    [RcloneFact]
    public async Task A_leftover_from_a_crashed_host_is_ended_at_the_next_start()
    {
        // Stands in for a crashed Host: an rclone whose pid file stays, started outside any tie.
        var leftover = await RcloneProcess.StartAsync(RcloneBinary.Path!, _state, Log);
        var oldPid = leftover.ProcessId;
        var pidFile = Path.Combine(_state, RcloneProcess.PidFileName);
        var saved = await File.ReadAllTextAsync(pidFile);

        await using var next = await RcloneProcess.StartAsync(RcloneBinary.Path!, _state, Log);
        Assert.False(IsRunning(oldPid));
        Assert.Contains(_log, l => l.Contains($"pid {oldPid}"));
        Assert.NotEqual(saved, await File.ReadAllTextAsync(pidFile));
        await leftover.DisposeAsync();
    }

    [RcloneFact]
    public async Task A_pid_file_naming_another_program_is_left_alone()
    {
        Directory.CreateDirectory(_state);
        using var self = Process.GetCurrentProcess();
        var pidFile = Path.Combine(_state, RcloneProcess.PidFileName);
        await File.WriteAllTextAsync(pidFile, $"{self.Id}\n{self.StartTime.ToUniversalTime().Ticks}");
        RcloneProcess.EndLeftover(pidFile, Log);
        Assert.False(File.Exists(pidFile));
        Assert.False(self.HasExited);
    }

    [RcloneFact]
    public async Task On_Linux_rclone_dies_with_the_process_that_started_it()
    {
        if (!OperatingSystem.IsLinux()) return;
        // tools/RcloneProbe stands in for the Host: it starts rclone through RcloneProcess and is then killed.
        var probe = RcloneBinary.ProbeCommand();
        Assert.True(probe is not null, "RcloneProbe.dll is not next to the tests; the project reference copies it.");
        var info = new ProcessStartInfo(probe!.Value.File) { RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var argument in probe.Value.Arguments) info.ArgumentList.Add(argument);
        info.ArgumentList.Add(RcloneBinary.Path!);
        info.ArgumentList.Add(_state);
        using var parent = Process.Start(info)!;
        var line = await parent.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(line);
        var childPid = int.Parse(line!);
        Assert.True(IsRunning(childPid));

        parent.Kill(); // SIGKILL: no ProcessExit handler runs, as in a crash
        await parent.WaitForExitAsync();
        await WaitFor(() => !IsRunning(childPid));
    }

    private static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            // A zombie (dead, not yet reaped by its parent) still has a /proc entry.
            if (OperatingSystem.IsLinux())
            {
                var stat = File.ReadAllText($"/proc/{pid}/stat");
                return stat[(stat.LastIndexOf(')') + 2)] != 'Z';
            }

            return true;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > until) throw new TimeoutException("The condition did not become true.");
            await Task.Delay(20);
        }
    }
}

/// <summary>A fact that needs a real rclone; skipped, saying why, without one.</summary>
public sealed class RcloneFactAttribute : FactAttribute
{
    public RcloneFactAttribute()
    {
        if (RcloneBinary.Path is null) Skip = RcloneBinary.Missing;
    }
}

internal static class RcloneBinary
{
    public static readonly string? Path = Find();

    public const string Missing = "No rclone: set INBOUND_RCLONE to an rclone 1.75.0 or later (it is not in the repository).";

    private static string? Find()
    {
        var fromEnv = Environment.GetEnvironmentVariable(RcloneSetup.RcloneVariable);
        return !string.IsNullOrEmpty(fromEnv) && File.Exists(fromEnv) ? fromEnv : null;
    }

    /// <summary>The probe program that starts rclone through <see cref="RcloneProcess"/> and prints its pid; null when not built.</summary>
    public static (string File, string[] Arguments)? ProbeCommand()
    {
        var dll = System.IO.Path.Combine(AppContext.BaseDirectory, "RcloneProbe.dll");
        return File.Exists(dll) ? ("dotnet", [dll]) : null;
    }
}
