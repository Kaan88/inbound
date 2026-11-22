using Android.Content;
using Android.Text;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile.TorBox;
using Google.Android.Material.Dialog;
using Google.Android.Material.TextField;

namespace Inbound.App.Screens;

/// <summary>Runs a Host request off the UI thread and hands its result back on it (as New download does), with failures as plain words.</summary>
internal static class TorBoxUi
{
    public static void Run<T>(MainActivity activity, Func<Task<T>> work, Action<T> done, Action<string> failed)
    {
        _ = Task.Run(async () =>
        {
            T result;
            try { result = await work().ConfigureAwait(false); }
            catch (Exception e)
            {
                // A request cut by its deadline or a closed connection still says so, or its buttons stay disabled (Fable A9-9).
                var words = e is OperationCanceledException or InvalidOperationException or InvalidDataException or IOException or TimeoutException or System.Text.Json.JsonException
                    ? Words(e) : "That could not be done.";
                activity.RunOnUiThread(() => { if (!activity.IsFinishing && !activity.IsDestroyed) failed(words); });
                return;
            }

            activity.RunOnUiThread(() => { if (!activity.IsFinishing && !activity.IsDestroyed) done(result); });
        });
    }

    private static string Words(Exception e) => e is InvalidOperationException and not ObjectDisposedException
        ? e.Message : "The Host did not answer. Check the connection and try again.";
}

/// <summary>
/// The sheet where the TorBox API key is entered, replaced or cleared. The key is masked while typed, goes only to the Host, is wiped
/// from the field as soon as it is sent and is never logged or shown again.
/// </summary>
internal static class TorBoxKeySheet
{
    /// <param name="hasKey">A key is saved already: Change replaces it, and Remove key is offered.</param>
    /// <param name="saved">Runs on the UI thread after the Host took (or cleared) the key.</param>
    public static void Show(MainActivity activity, TorBoxClient client, bool hasKey, string hostName, Action saved)
    {
        var dp = (int d) => Motion.Dp(activity, d);
        var layout = (TextInputLayout)Android.Views.LayoutInflater.From(activity)!.Inflate(Resource.Layout.password_field, null, false)!;
        layout.Hint = "TorBox API key";
        var input = layout.FindViewById<TextInputEditText>(Resource.Id.password_input)!;
        input.InputType = InputTypes.ClassText | InputTypes.TextVariationPassword;
        var error = new TextView(activity) { Visibility = ViewStates.Gone, AccessibilityLiveRegion = AccessibilityLiveRegion.Polite };
        error.SetTextColor(new Android.Graphics.Color(Motion.Attr(activity, Resource.Attribute.colorError)));
        var body = new LinearLayout(activity) { Orientation = Orientation.Vertical };
        body.SetPadding(dp(24), dp(8), dp(24), 0);
        body.AddView(layout);
        body.AddView(error);
        var secure = activity.SecureHostConnection;
        if (!secure)
        {
            input.Enabled = false;
            var note = new TextView(activity) { Text = MainActivity.SecretNeedsHttps, TextSize = 14 };
            note.SetTextColor(new Android.Graphics.Color(Motion.Attr(activity, Resource.Attribute.colorOnSurfaceVariant)));
            body.AddView(note);
        }

        var builder = new MaterialAlertDialogBuilder(activity).SetTitle("TorBox account")!
            .SetMessage(hasKey
                ? "A key is saved and can't be shown again. Enter a new one to replace it."
                : "Enter your TorBox API key. You'll find it in your account settings on the TorBox site."
                  + (hostName.Length > 0 ? " It is saved on " + hostName + "." : ""))!
            .SetView(body)!
            .SetNegativeButton("Cancel", (IDialogInterfaceOnClickListener?)null)!
            .SetPositiveButton(hasKey ? "Replace" : "Save", (IDialogInterfaceOnClickListener?)null)!;
        if (hasKey) builder.SetNeutralButton("Remove key", (IDialogInterfaceOnClickListener?)null);
        var dialog = builder.Create();
        dialog.DismissEvent += (_, _) => input.Text = "";
        MainActivity.KeepSecret(dialog);
        dialog.Show();
        if (!secure && dialog.GetButton(-1) is { } send) send.Enabled = false;

        var busy = false;
        void Fail(string words)
        {
            busy = false;
            error.Text = words; error.Visibility = ViewStates.Visible;
            SetEnabled(true);
        }

        void SetEnabled(bool on)
        {
            foreach (var b in new[] { -1, -2, -3 }) if (dialog.GetButton(b) is { } button) button.Enabled = on && (b != -1 || secure);
            // Backing out mid-save would still open the TorBox page on success (Fable A9-12).
            dialog.SetCancelable(on);
        }

        void Done(RemoteAccountDone result, Action<string> fail)
        {
            if (result.Ok) { dialog.Dismiss(); saved(); }
            else fail(TorBoxText.Failure(result.Error));
        }

        dialog.GetButton(-1)!.Click += (_, _) =>
        {
            var key = input.Text?.Trim() ?? "";
            if (busy || !secure) return;
            if (key.Length == 0) { Fail("Enter the key first."); return; }
            busy = true; error.Visibility = ViewStates.Gone; SetEnabled(false);
            // The text leaves the field now: only the request holds it, and nothing is logged.
            input.Text = "";
            TorBoxUi.Run(activity, async () =>
            {
                var r = await client.SetKeyAsync(key).ConfigureAwait(false);
                return new RemoteAccountDone(r.Ok, r.Error ?? (r.Ok ? null : TorBoxText.Failure(null, r.FailureKind)));
            }, r => Done(r, Fail), Fail);
        };
        if (dialog.GetButton(-3) is { } remove)
            remove.Click += (_, _) =>
            {
                if (busy) return;
                busy = true; error.Visibility = ViewStates.Gone; SetEnabled(false);
                TorBoxUi.Run(activity, async () =>
                {
                    var r = await client.ClearKeyAsync().ConfigureAwait(false);
                    return new RemoteAccountDone(r.Ok, r.Error);
                }, r => Done(r, Fail), Fail);
            };
        input.RequestFocus();
    }

    private sealed record RemoteAccountDone(bool Ok, string? Error);
}
