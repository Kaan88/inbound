#if INBOUND_TEST_BUILD
using Android.Content;
using Android.OS;
using Android.Widget;
using DownloadManager.Mobile.Updates;
using Inbound.App.Screens;

namespace Inbound.App;

// UI-only fixture. No updater ReadyOffer, trust override, network call or installation is created.
public sealed partial class MainActivity
{
    private AndroidX.AppCompat.App.AlertDialog? _updatePreview;
    private bool _suspendingUpdatePreview;
    private int _previewOuterScroll, _previewNotesScroll;
    private string? _updatePreviewKind;
    private void SaveUpdatePreview(Bundle state)
    {
        if (_updatePreviewKind is null) return;
        if (_updatePreview is not null) (_previewOuterScroll, _previewNotesScroll) = UpdateReadyDialog.CaptureScroll(_updatePreview);
        state.PutString("update_preview_kind", _updatePreviewKind);
        state.PutInt("update_preview_outer_scroll", _previewOuterScroll); state.PutInt("update_preview_notes_scroll", _previewNotesScroll);
    }
    private void SuspendUpdatePreview()
    {
        if (_updatePreview is null) return;
        (_previewOuterScroll, _previewNotesScroll) = UpdateReadyDialog.CaptureScroll(_updatePreview);
        _suspendingUpdatePreview = true; _updatePreview.Dismiss(); _updatePreview = null; _suspendingUpdatePreview = false;
    }
    private void ResumeUpdatePreview()
    {
        if (_updatePreviewKind is null || _updatePreview is not null) return;
        var state = new Bundle(); SaveUpdatePreview(state); HandleUpdatePreview(null, state);
    }
    private void HandleUpdatePreview(Intent? intent, Bundle? saved)
    {
        var kind = intent?.GetStringExtra("update_prompt_preview") ?? saved?.GetString("update_preview_kind");
        intent?.RemoveExtra("update_prompt_preview");
        if (kind is not ("short" or "long" or "none")) return;
        var outer = saved?.GetInt("update_preview_outer_scroll") ?? 0;
        var noteScroll = saved?.GetInt("update_preview_notes_scroll") ?? 0;
        FindViewById<Android.Views.View>(Resource.Id.root)!.Post(() =>
        {
            if (IsDestroyed || IsFinishing) return;
            _updatePreview?.Dismiss();
            var notes = "• A clearer update prompt with a changelog.\n\n• Queue and schedule reliability fixes.\n\n• Your downloads and settings are kept.";
            if (kind == "long") notes = string.Join("\n\n", Enumerable.Range(1, 20).Select(i => $"• Example change {i}: improve queues, schedules and downloads while keeping your settings."));
            var offer = new AndroidUpdateManifest { VersionCode = 6, VersionName = "0.0.6", Size = 20599175, ReleaseNotes = kind == "none" ? null : notes };
            var dialog = UpdateReadyDialog.Create(this, offer,
                () => Toast.MakeText(this, "Preview only; no installation started.", ToastLength.Short)?.Show());
            _updatePreview = dialog; _updatePreviewKind = kind;
            dialog.DismissEvent += (_, _) => { if (ReferenceEquals(_updatePreview, dialog)) { _updatePreview = null; if (!_suspendingUpdatePreview) _updatePreviewKind = null; } };
            dialog.Show(); UpdateReadyDialog.RestoreScroll(dialog, outer, noteScroll);
        });
    }
}
#endif
