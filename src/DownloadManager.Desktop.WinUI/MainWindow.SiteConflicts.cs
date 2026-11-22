using DownloadManager.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace DownloadManager.Desktop.WinUI;

/// <summary>
/// SITES.11 R5 (delete-mockup.png, overwrite-mockup.png): the overwrite question one file at a time and the delete
/// confirmation of both panes. Every dialog's content is built fresh for each ask (an element can have one parent only), goes
/// through <see cref="DialogGuard"/> and takes the window's XamlRoot. All wording is in SitesCommandRules and SiteChangeRules.
/// </summary>
public sealed partial class MainWindow
{
    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private sealed record OverwriteAnswer(string Choice, bool ApplyToOthers);

    // ---- The overwrite question --------------------------------------------------------------------------

    /// <summary>
    /// Asks about each name that is already in the target, one dialog per file (a folder: one for its contents); "Do this for the
    /// other N files" ends the asking. Null on Cancel. Names the target does not have are not asked about. When the target is
    /// only partly known and nothing is known to be there, the batch is asked once (the R2b question). Other is the choice for
    /// names the target did not list: Skip, or the answer "Do this for the others" applied when the target is partly listed.
    /// <paramref name="preciseTimes"/>: the remote's file times are good enough to recommend Replace on.
    /// </summary>
    private async Task<(Dictionary<string, string> Choices, string Other)?> AskConflictsAsync(List<SitesCommandRules.ConflictPair> pairs, bool upload,
        string place, bool hasFolders, bool complete, IReadOnlyCollection<string> taken, bool preciseTimes)
    {
        var choices = new Dictionary<string, string>(StringComparer.Ordinal);
        if (pairs.Count == 0)
        {
            if (SitesCommandRules.ConflictQuestion([], place, hasFolders, complete) is not { } ask) return (choices, SitesCommandRules.Skip);
            if (await AskConflictAsync(ask) is not { } all) return null;
            return (choices, all);
        }

        var note = complete ? null : $"Only part of {place} is listed, so others may be there too.";
        string? appliedToOthers = null;
        for (var i = 0; i < pairs.Count; i++)
        {
            var pair = pairs[i];
            var rest = pairs.Skip(i + 1).ToList();
            // A name is promised only when the Host is sure to give that one; otherwise "under a numbered name".
            var keepName = pair.IsFolder ? null : SitesCommandRules.ShownKeepBothName(pair.Name, taken, complete);
            var answer = await AskOverwriteAsync(pair, upload, keepName, preciseTimes, rest.Count, rest.Any(p => p.IsFolder), i == 0 ? note : null);
            if (answer is null) return null;
            choices[pair.Name] = answer.Choice;
            if (!answer.ApplyToOthers) continue;
            foreach (var other in rest) choices[other.Name] = answer.Choice;
            appliedToOthers = answer.Choice;
            break;
        }

        return (choices, SitesCommandRules.ChoiceForUnlisted(complete, appliedToOthers));
    }

    /// <summary>One file's question: direction icon and title, the two copies' rows, three choices with the safer one marked, and the tick for the rest.</summary>
    private async Task<OverwriteAnswer?> AskOverwriteAsync(SitesCommandRules.ConflictPair pair, bool upload, string? keepName, bool preciseTimes, int others,
        bool othersHaveFolder, string? note)
    {
        var secondary = ThemeBrush("InboundSecondaryTextBrush");
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        title.Children.Add(new FontIcon { Glyph = upload ? "" : "", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        title.Children.Add(new TextBlock { Text = SitesCommandRules.OverwriteTitle(pair, upload), TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });

        var body = new StackPanel { Spacing = 10, MinWidth = 420 };
        if (pair.IsFolder) body.Children.Add(new TextBlock { Text = SitesCommandRules.FoldersFollowNote, TextWrapping = TextWrapping.Wrap });
        if (note is not null) body.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = secondary, TextWrapping = TextWrapping.Wrap });
        var rows = SitesCommandRules.OverwriteRows(pair, upload);
        if (rows.Count > 0)
        {
            var grid = new Grid { ColumnSpacing = 24, RowSpacing = 4 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            for (var row = 0; row < rows.Count; row++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var label = new TextBlock { Text = rows[row].Label, Foreground = secondary };
                var value = new TextBlock { Text = rows[row].Text, TextWrapping = TextWrapping.Wrap };
                Grid.SetRow(label, row);
                Grid.SetRow(value, row);
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }

            body.Children.Add(grid);
        }

        var group = "overwrite" + Guid.NewGuid().ToString("N");
        var choices = SitesCommandRules.OverwriteChoices(pair, upload, keepName, preciseTimes);
        var buttons = new List<RadioButton>();
        var radios = new StackPanel { Spacing = 0 };
        foreach (var choice in choices)
        {
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
            text.Inlines.Add(new Run { Text = choice.Label });
            if (choice.Recommended) text.Inlines.Add(new Run { Text = SitesCommandRules.RecommendedMark, Foreground = secondary });
            var radio = new RadioButton { GroupName = group, Content = text, IsChecked = choice.Recommended, MinHeight = 32 };
            buttons.Add(radio);
            radios.Children.Add(radio);
        }

        body.Children.Add(radios);
        CheckBox? rest = null;
        if (SitesCommandRules.ApplyToOthersLabel(others, othersHaveFolder) is { } restLabel)
        {
            rest = new CheckBox { Content = restLabel, MinHeight = 32 };
            body.Children.Add(rest);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = Content?.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = body,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await DialogGuard.ShowAsync(dialog);
        if (result is null)
        {
            SetSitesStatus("Another dialog is open; close it and try again.");
            return null;
        }

        if (result != ContentDialogResult.Primary) return null;
        var picked = Math.Max(0, buttons.FindIndex(b => b.IsChecked == true));
        return new OverwriteAnswer(choices[picked].Value, rest?.IsChecked == true);
    }

    /// <summary>The batch question for a target only partly listed (R2b's, with no file to compare): Skip them, Replace, Keep both.</summary>
    private async Task<string?> AskConflictAsync(SitesCommandRules.ConflictAsk ask)
    {
        var body = new StackPanel { Spacing = 10 };
        foreach (var note in ask.Notes) body.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap });
        var choices = new RadioButtons();
        foreach (var (_, label) in SitesCommandRules.ConflictChoices) choices.Items.Add(label);
        choices.SelectedIndex = 0;
        body.Children.Add(choices);
        var dialog = new ContentDialog
        {
            XamlRoot = Content?.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = ask.Title,
            Content = body,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        var answer = await DialogGuard.ShowAsync(dialog);
        if (answer is null)
        {
            SetSitesStatus("Another dialog is open; close it and try again.");
            return null;
        }

        if (answer != ContentDialogResult.Primary) return null;
        return SitesCommandRules.ConflictChoices[Math.Max(0, choices.SelectedIndex)].Value;
    }

    /// <summary>Sends one batch per choice (the Host takes one choice per batch); the last batch started, or null when none did.</summary>
    private async Task<SiteTransferDto?> StartConflictBatchesAsync(IReadOnlyList<SiteTransferNameDto> names, Dictionary<string, string> choices, string other,
        Func<string, List<SiteTransferNameDto>, SiteTransferStartRequest> build)
    {
        var batches = SitesCommandRules.ConflictBatches(names, choices, other);
        if (batches.Count == 0)
        {
            SetSitesStatus("Everything was skipped.");
            return null;
        }

        SiteTransferDto? last = null;
        foreach (var batch in batches)
        {
            var started = await StartSiteTransferAsync(build(batch.Conflict, [.. batch.Names]));
            if (started is null) break;
            last = started;
        }

        return last;
    }

    /// <summary>What is known of each picked name on this PC (an upload's source): size and last-changed. Unreadable names are left out.</summary>
    private static Dictionary<string, SitesCommandRules.ConflictSide> LocalSides(string folder, IEnumerable<SiteTransferNameDto> names)
    {
        var sides = new Dictionary<string, SitesCommandRules.ConflictSide>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            try
            {
                var path = Path.Combine(folder, name.Name);
                if (name.IsFolder) sides[name.Name] = new SitesCommandRules.ConflictSide(-1, null);
                else if (new FileInfo(path) is { Exists: true } file) sides[name.Name] = new SitesCommandRules.ConflictSide(file.Length, file.LastWriteTimeUtc);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
            }
        }

        return sides;
    }

    /// <summary>The files and folders in a folder on this PC with size and last-changed (a download's target); empty when it can't be read.</summary>
    private static List<KeyValuePair<string, SitesCommandRules.ConflictSide>> LocalTargets(string folder)
    {
        var found = new List<KeyValuePair<string, SitesCommandRules.ConflictSide>>();
        try
        {
            if (!Directory.Exists(folder)) return found;
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                var size = entry is FileInfo file ? file.Length : -1;
                found.Add(new(entry.Name, new SitesCommandRules.ConflictSide(size, entry.LastWriteTimeUtc)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }

        return found;
    }

    // ---- Delete ------------------------------------------------------------------------------------------

    private static Style? DangerButtonStyle()
    {
        try
        {
            var style = new Style(typeof(Button));
            if (Application.Current.Resources.TryGetValue("DefaultButtonStyle", out var basis) && basis is Style basedOn) style.BasedOn = basedOn;
            if (ThemeBrush("InboundIconDangerBrush") is { } danger)
            {
                style.Setters.Add(new Setter(Control.ForegroundProperty, danger));
                style.Setters.Add(new Setter(Control.BorderBrushProperty, danger));
            }

            // The bin before the words: a dialog button's text can't carry an icon, so its content template does.
            style.Setters.Add(new Setter(ContentControl.ContentTemplateProperty, (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><StackPanel Orientation='Horizontal' Spacing='8'>"
                + "<FontIcon Glyph='&#xE74D;' FontSize='14'/><TextBlock Text='{Binding}'/></StackPanel></DataTemplate>")));
            return style;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or Microsoft.UI.Xaml.Markup.XamlParseException)
        {
            return null;
        }
    }

    private static void FillLead(TextBlock block, List<SiteChangeRules.TextPart> parts)
    {
        block.Inlines.Clear();
        foreach (var part in parts)
            block.Inlines.Add(part.Bold ? new Run { Text = part.Text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold } : new Run { Text = part.Text });
    }

    /// <summary>
    /// The delete confirmation, for the server, the Recycle Bin, a network folder on this PC, or Delete permanently
    /// (<see cref="SiteChangeRules.DeletePlace"/>). The server and Delete permanently count what goes while the dialog is up
    /// (<paramref name="count"/>: "Counting…", then the numbers, or "at least" when it fell short), and a folder in the choice
    /// needs its tick before the button works; the count is cancelled when the dialog closes. Cancel is the default; a stray
    /// Enter never deletes. False when another dialog is open (the status line says so).
    /// </summary>
    private async Task<bool> ConfirmDeleteAsync(IReadOnlyList<SiteTransferNameDto> chosen, SiteChangeRules.DeletePlace place, string? remoteName,
        Func<CancellationToken, Task<SiteCountResultDto>>? count)
    {
        var hasFolder = chosen.Any(item => item.IsFolder);
        var counts = SiteChangeRules.DeleteCounts(place) && count is not null;
        SiteChangeRules.DeleteCount? found = null;

        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        title.Children.Add(new FontIcon
        {
            Glyph = "", FontSize = 18, VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("SystemFillColorCautionBrush") ?? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF5, 0xB2, 0x2D)),
        });
        title.Children.Add(new TextBlock { Text = SiteChangeRules.DeleteTitle(place), VerticalAlignment = VerticalAlignment.Center });

        var body = new StackPanel { Spacing = 12, MinWidth = 420 };
        var lead = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var warning = new TextBlock { Text = SiteChangeRules.DeleteWarning(place, chosen.Count), TextWrapping = TextWrapping.Wrap };
        var note = new TextBlock { FontSize = 12, Foreground = ThemeBrush("InboundSecondaryTextBrush"), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var tick = new CheckBox { MinHeight = 32 };
        body.Children.Add(lead);
        body.Children.Add(warning);
        body.Children.Add(note);
        var needsTick = SiteChangeRules.DeleteCounts(place) && hasFolder;
        if (needsTick) body.Children.Add(tick);

        var dialog = new ContentDialog
        {
            XamlRoot = Content?.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = title,
            Content = body,
            PrimaryButtonText = SiteChangeRules.DeleteButton(place),
            PrimaryButtonStyle = DangerButtonStyle(),
            CloseButtonText = "Cancel",
            // Delete never goes by a stray Enter.
            DefaultButton = ContentDialogButton.Close,
            IsPrimaryButtonEnabled = !needsTick,
        };

        void Refresh()
        {
            FillLead(lead, SiteChangeRules.DeleteLead(chosen, found, place, remoteName));
            var text = counts ? SiteChangeRules.CountNote(chosen, found) : null;
            note.Text = text ?? "";
            note.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
            tick.Content = SiteChangeRules.DeleteTick(chosen, found) ?? "";
            // The tick waits for the count, so the number it names has been seen.
            tick.IsEnabled = !counts || found is not null;
        }

        void ApplyTick() => dialog.IsPrimaryButtonEnabled = !needsTick || tick.IsChecked == true;
        tick.Checked += (_, _) => ApplyTick();
        tick.Unchecked += (_, _) => ApplyTick();
        Refresh();

        var open = true;
        using var stop = new CancellationTokenSource();
        if (counts) _ = CountIntoAsync();

        async Task CountIntoAsync()
        {
            SiteChangeRules.DeleteCount result;
            try
            {
                result = SiteChangeRules.CountOf(await count!(stop.Token));
            }
            catch (Exception)
            {
                result = SiteChangeRules.CountOf(new SiteCountResultDto { Ok = false });
            }

            if (!open) return;
            found = result;
            Refresh();
        }

        ContentDialogResult? answer;
        try
        {
            answer = await DialogGuard.ShowAsync(dialog);
        }
        finally
        {
            open = false;
            // The count is not wanted any more (sites.count on the Host, or the walk on this PC).
            stop.Cancel();
        }

        if (answer is null)
        {
            SetSitesStatus("Another dialog is open; close it and try again.");
            return false;
        }

        return answer == ContentDialogResult.Primary;
    }
}
