using DownloadManager.Contracts;
using DownloadManager.Desktop.WinUI;

namespace DownloadManager.Core.Tests;

/// <summary>UI.R26: the server switcher's list, its Ctrl+number keys, and the window title that says which server is shown.</summary>
public sealed class ServerSwitchTests
{
    private static SavedServer Saved(string id, string name) => new() { Id = id, Name = name, Address = "http://homeserver:8080/" };

    [Fact]
    public void ThisPcComesFirstWithCtrlZeroThenTheServersInTheirOrder()
    {
        var choices = ServerSwitch.Choices([Saved("aa", "Home server"), Saved("bb", "Office")], shownId: null);

        Assert.Equal(["This PC", "Home server", "Office"], choices.Select(choice => choice.Name));
        Assert.Equal(["Ctrl+0", "Ctrl+1", "Ctrl+2"], choices.Select(choice => choice.ShortcutText));
        Assert.True(choices[0].IsLocal);
        Assert.True(choices[0].IsCurrent);
        Assert.DoesNotContain(choices.Skip(1), choice => choice.IsCurrent);
    }

    [Fact]
    public void TheShownServerIsTheOnlyCurrentOneWhateverTheCaseOfItsId()
    {
        var choices = ServerSwitch.Choices([Saved("aa", "Home server"), Saved("bb", "Office")], shownId: "BB");

        Assert.Equal([false, false, true], choices.Select(choice => choice.IsCurrent));
    }

    [Fact]
    public void AServerShownThatIsNoLongerSavedLeavesNoneTicked()
    {
        var choices = ServerSwitch.Choices([Saved("aa", "Home server")], shownId: "gone");

        Assert.DoesNotContain(choices, choice => choice.IsCurrent);
    }

    [Fact]
    public void ServersAfterTheNinthAreListedWithoutAKey()
    {
        var saved = Enumerable.Range(1, 11).Select(number => Saved($"id{number}", $"Server {number}")).ToList();

        var choices = ServerSwitch.Choices(saved, shownId: null);

        Assert.Equal(12, choices.Count);
        Assert.Equal("Ctrl+9", choices[9].ShortcutText);
        Assert.Null(choices[10].Number);
        Assert.Null(choices[11].ShortcutText);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(1, "aa")]
    [InlineData(2, "bb")]
    public void CtrlNumberGoesToItsChoice(int number, string? id)
    {
        var choices = ServerSwitch.Choices([Saved("aa", "Home server"), Saved("bb", "Office")], shownId: null);

        var choice = ServerSwitch.ForNumber(choices, number);

        Assert.NotNull(choice);
        Assert.Equal(id, choice.Id);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(-1)]
    public void CtrlNumberWithNoServerThereGoesNowhere(int number)
    {
        var choices = ServerSwitch.Choices([Saved("aa", "Home server"), Saved("bb", "Office")], shownId: null);

        Assert.Null(ServerSwitch.ForNumber(choices, number));
    }

    [Fact]
    public void ThisPcKeepsThePlainTitleAndAServerIsNamedFirst()
    {
        var server = ServerTarget.Remote("aa", "Home server", new Uri("http://homeserver:8080/"), "token-value", plainHttpConfirmed: true);

        Assert.Equal("Inbound Download Manager", ServerSwitch.TitleFor(ServerTarget.Local));
        Assert.Equal("Home server — Inbound Download Manager", ServerSwitch.TitleFor(server));
        Assert.DoesNotContain("token-value", ServerSwitch.TitleFor(server));
        Assert.Contains("Home server", ServerSwitch.SwitcherHelp(server));
        Assert.Contains("Ctrl+Shift+O", ServerSwitch.SwitcherHelp(ServerTarget.Local));
    }
}
