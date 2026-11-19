using System.Text.Json;
using DownloadManager.Contracts;
using DownloadManager.Mobile;

namespace DownloadManager.Mobile.Tests;

public class QueueUserStartTests
{
    [Fact]
    public void A_trimmed_case_insensitive_queue_start_is_a_user_start()
    {
        var request = IpcMessage.Create(MessageTypes.QueueAction, new QueueActionRequest { Action = "  START  " });

        Assert.True(CrashGuard.IsUserStart(request));
    }

    [Fact]
    public void Resuming_a_download_group_is_a_user_start()
    {
        var request = IpcMessage.Create(MessageTypes.GroupAction, new GroupActionRequest { Action = "resume" });

        Assert.True(CrashGuard.IsUserStart(request));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("create")]
    [InlineData("policy")]
    [InlineData("delete")]
    public void Queue_actions_other_than_start_do_not_reset_the_guard(string action)
    {
        var request = IpcMessage.Create(MessageTypes.QueueAction, new QueueActionRequest { Action = action });

        Assert.False(CrashGuard.IsUserStart(request));
    }

    [Theory]
    [InlineData("pause")]
    [InlineData("create")]
    public void Group_actions_other_than_resume_do_not_reset_the_guard(string action)
    {
        var request = IpcMessage.Create(MessageTypes.GroupAction, new GroupActionRequest { Action = action });

        Assert.False(CrashGuard.IsUserStart(request));
    }

    [Theory]
    [InlineData(MessageTypes.QueueAction)]
    [InlineData(MessageTypes.GroupAction)]
    public void A_command_without_a_payload_does_not_reset_the_guard(string type)
    {
        Assert.False(CrashGuard.IsUserStart(IpcMessage.Create(type)));
    }

    [Theory]
    [InlineData(MessageTypes.QueueAction, "[]")]
    [InlineData(MessageTypes.GroupAction, "42")]
    public void An_unexpected_payload_shape_does_not_reset_the_guard(string type, string json)
    {
        using var document = JsonDocument.Parse(json);
        var request = new IpcMessage(Protocol.Version, type, "test", null, document.RootElement.Clone());

        Assert.False(CrashGuard.IsUserStart(request));
    }

    [Theory]
    [InlineData(MessageTypes.QueueAction, "")]
    [InlineData(MessageTypes.QueueAction, "   ")]
    [InlineData(MessageTypes.GroupAction, "")]
    [InlineData(MessageTypes.GroupAction, "   ")]
    public void An_empty_action_does_not_reset_the_guard(string type, string action)
    {
        Assert.False(CrashGuard.IsUserStart(Request(type, action)));
    }

    [Theory]
    [InlineData(MessageTypes.QueueAction)]
    [InlineData(MessageTypes.GroupAction)]
    public void A_null_action_does_not_reset_the_guard(string type)
    {
        Assert.False(CrashGuard.IsUserStart(Request(type, null!)));
    }

    [Theory]
    [InlineData(MessageTypes.JobAdd)]
    [InlineData(MessageTypes.JobResume)]
    [InlineData(MessageTypes.JobRetry)]
    [InlineData(MessageTypes.JobRedownload)]
    [InlineData(MessageTypes.JobsResumeAll)]
    public void Existing_user_start_commands_still_reset_the_guard(string type)
    {
        Assert.True(CrashGuard.IsUserStart(IpcMessage.Create(type)));
    }

    private static IpcMessage Request(string type, string action) => type switch
    {
        MessageTypes.QueueAction => IpcMessage.Create(type, new QueueActionRequest { Action = action }),
        MessageTypes.GroupAction => IpcMessage.Create(type, new GroupActionRequest { Action = action }),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}