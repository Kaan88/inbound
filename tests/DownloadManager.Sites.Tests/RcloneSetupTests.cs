using DownloadManager.Sites;

namespace DownloadManager.Sites.Tests;

/// <summary>Which rclone is run, how its version is read, and the environment it gets. No real files or processes.</summary>
public sealed class RcloneSetupTests
{
    private static Version V(int major, int minor, int patch) => new(major, minor, patch);

    [Fact]
    public void Own_path_wins_over_the_environment_and_the_bundled_copy()
    {
        var files = new HashSet<string> { "/custom/rclone", "/env/rclone", "/opt/app/tools/rclone/rclone" };
        var found = RcloneSetup.Find("/opt/app", "/custom/rclone", "/env/rclone", windows: false, files.Contains);
        Assert.Equal("/custom/rclone", found);
    }

    [Fact]
    public void A_set_own_path_that_is_missing_returns_null_instead_of_falling_back()
    {
        var files = new HashSet<string> { "/env/rclone", "/opt/app/tools/rclone/rclone" };
        var found = RcloneSetup.Find("/opt/app", "/missing/rclone", "/env/rclone", windows: false, files.Contains);
        Assert.Null(found);
    }

    [Fact]
    public void The_environment_variable_is_used_when_there_is_no_own_path()
    {
        var files = new HashSet<string> { "/env/rclone", "/opt/app/tools/rclone/rclone" };
        var found = RcloneSetup.Find("/opt/app", null, "/env/rclone", windows: false, files.Contains);
        Assert.Equal("/env/rclone", found);
    }

    [Theory]
    [InlineData("/opt/app")]
    [InlineData("/opt/app/")]
    public void The_bundled_copy_is_found_on_linux_with_or_without_a_trailing_separator(string baseDirectory)
    {
        var files = new HashSet<string> { "/opt/app/tools/rclone/rclone" };
        var found = RcloneSetup.Find(baseDirectory, null, null, windows: false, files.Contains);
        Assert.Equal("/opt/app/tools/rclone/rclone", found);
    }

    [Theory]
    [InlineData(@"C:\app")]
    [InlineData(@"C:\app\")]
    public void The_bundled_copy_is_found_on_windows_with_or_without_a_trailing_separator(string baseDirectory)
    {
        var files = new HashSet<string> { @"C:\app\tools\rclone\rclone.exe" };
        var found = RcloneSetup.Find(baseDirectory, null, null, windows: true, files.Contains);
        Assert.Equal(@"C:\app\tools\rclone\rclone.exe", found);
    }

    [Fact]
    public void Nothing_found_returns_null()
    {
        var found = RcloneSetup.Find("/opt/app", null, null, windows: false, _ => false);
        Assert.Null(found);
    }

    [Theory]
    [InlineData("rclone v1.76.0\n- os/version: linux\n", 1, 76, 0)]
    [InlineData("rclone v1.76.0-beta.8899.abcdef.master\n", 1, 76, 0)]
    [InlineData("rclone v1.76.0-DEV", 1, 76, 0)]
    [InlineData("  rclone v1.76.0\r\n- os/version: windows\r\n", 1, 76, 0)]
    public void ParseVersion_reads_the_release_from_the_first_line(string output, int major, int minor, int patch)
    {
        Assert.Equal(V(major, minor, patch), RcloneSetup.ParseVersion(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("rclone 1.76.0")]
    [InlineData("something v1.76.0")]
    [InlineData("rclone v1.76")]
    [InlineData("rclone v99999999999.0.0")]
    public void ParseVersion_returns_null_for_text_that_is_not_a_version_line(string output)
    {
        Assert.Null(RcloneSetup.ParseVersion(output));
    }

    [Fact]
    public void Versions_from_1_75_0_up_are_accepted_for_sftp()
    {
        Assert.Null(RcloneSetup.TooOldForSftp(V(1, 75, 0)));
        Assert.Null(RcloneSetup.TooOldForSftp(V(1, 76, 0)));
    }

    [Fact]
    public void An_older_rclone_is_refused_for_sftp_with_both_versions_in_the_message()
    {
        var message = RcloneSetup.TooOldForSftp(V(1, 74, 3));
        Assert.NotNull(message);
        Assert.Contains("1.74.3", message);
        Assert.Contains("1.75.0", message);
    }

    [Fact]
    public void Clean_removes_rclone_variables_in_any_case_and_keeps_the_rest()
    {
        var environment = new Dictionary<string, string?>
        {
            ["PATH"] = "/usr/bin",
            ["HOME"] = "/home/user",
            ["RCLONE_CONFIG"] = "/secret/rclone.conf",
            ["rclone_rc_pass"] = "lowercase-pass",
        };

        RcloneEnvironment.Clean(environment);

        Assert.Equal(2, environment.Count);
        Assert.Equal("/usr/bin", environment["PATH"]);
        Assert.Equal("/home/user", environment["HOME"]);
        Assert.False(environment.ContainsKey("RCLONE_CONFIG"));
        Assert.False(environment.ContainsKey("rclone_rc_pass"));
    }
}
