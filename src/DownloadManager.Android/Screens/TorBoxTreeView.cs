using Android.Content;
using Android.Graphics;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile;
using DownloadManager.Mobile.TorBox;
using Google.Android.Material.CheckBox;

namespace Inbound.App.Screens;

/// <summary>The file tree of a cached torrent or a TorBox job as rows with check boxes, sizes and folders that open and close (t2).</summary>
internal static class TorBoxTreeView
{
    /// <summary>Fills <paramref name="list"/> with the rows now visible; <paramref name="changed"/> runs after a check changes (totals and buttons follow).</summary>
    public static void Render(Context c, LinearLayout list, TorBoxFileTree tree, bool readOnly, Action changed)
    {
        list.RemoveAllViews();
        foreach (var (node, depth) in tree.Visible()) list.AddView(Row(c, list, tree, node, depth, readOnly, changed));
    }

    private static View Row(Context c, LinearLayout list, TorBoxFileTree tree, TorBoxNode node, int depth, bool readOnly, Action changed)
    {
        var dp = (int d) => Motion.Dp(c, d);
        var row = new LinearLayout(c) { Orientation = Orientation.Horizontal, Clickable = true, Focusable = true };
        row.SetGravity(GravityFlags.CenterVertical);
        row.SetMinimumHeight(dp(48));
        row.SetPadding(dp(10 + depth * 18), 0, dp(14), 0);
        row.Background = new TypedValueHolder(c).SelectableBackground();

        var state = tree.StateOf(node);
        var box = new MaterialCheckBox(c)
        {
            CheckedState = state switch { TorBoxCheckState.Checked => MaterialCheckBox.StateChecked, TorBoxCheckState.Partial => MaterialCheckBox.StateIndeterminate, _ => MaterialCheckBox.StateUnchecked },
            Enabled = !readOnly && (node.IsFolder || node.Selectable),
            Focusable = false,
        };
        box.ContentDescription = (node.IsFolder ? "Folder " : "File ") + node.Name;
        row.AddView(box, new LinearLayout.LayoutParams(dp(40), dp(40)));

        var icon = new ImageView(c) { ImportantForAccessibility = ImportantForAccessibility.No };
        icon.SetImageResource(node.IsFolder ? Resource.Drawable.ic_folder : Resource.Drawable.ic_file);
        icon.ImageTintList = Android.Content.Res.ColorStateList.ValueOf(new Color(node.IsFolder
            ? c.GetColor(Resource.Color.state_waiting) : Motion.Attr(c, Resource.Attribute.colorOnSurfaceVariant)));
        row.AddView(icon, new LinearLayout.LayoutParams(dp(22), dp(22)) { MarginStart = dp(4), MarginEnd = dp(12) });

        var enabled = node.IsFolder || node.Selectable;
        var name = new TextView(c) { Text = node.Name, TextSize = 15 };
        name.SetSingleLine(true);
        name.Ellipsize = Android.Text.TextUtils.TruncateAt.Middle;
        name.SetTextColor(new Color(Motion.Attr(c, !enabled || state == TorBoxCheckState.Unchecked && !readOnly
            ? Resource.Attribute.colorOnSurfaceVariant : Resource.Attribute.colorOnSurface)));
        var text = new LinearLayout(c) { Orientation = Orientation.Vertical };
        text.AddView(name);
        if (!string.IsNullOrWhiteSpace(node.Note))
        {
            var note = new TextView(c) { Text = node.Note, TextSize = 12 };
            note.SetTextColor(new Color(c.GetColor(Resource.Color.state_failed)));
            text.AddView(note);
        }

        row.AddView(text, new LinearLayout.LayoutParams(0, -2, 1));
        var size = new TextView(c) { Text = Fmt.Bytes(node.Size), TextSize = 13 };
        size.SetTextColor(new Color(Motion.Attr(c, Resource.Attribute.colorOnSurfaceVariant)));
        row.AddView(size, new LinearLayout.LayoutParams(-2, -2) { MarginStart = dp(8) });

        row.ContentDescription = $"{(node.IsFolder ? "Folder" : "File")} {node.Name}, {Fmt.Bytes(node.Size)}"
            + (readOnly ? "" : state == TorBoxCheckState.Checked ? ", chosen" : state == TorBoxCheckState.Partial ? ", partly chosen" : ", not chosen");
        void Toggle()
        {
            if (readOnly) return;
            tree.Toggle(node);
            Render(c, list, tree, readOnly, changed);
            changed();
        }

        box.Click += (_, _) => Toggle();
        row.Click += (_, _) =>
        {
            // A folder's row opens and closes it; the check box is what chooses it. A file's row is its check box.
            if (node.IsFolder) { node.Expanded = !node.Expanded; Render(c, list, tree, readOnly, changed); }
            else Toggle();
        };
        return row;
    }
}
