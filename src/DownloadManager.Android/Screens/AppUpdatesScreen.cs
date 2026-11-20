using Android.OS;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile.Updates;
using Google.Android.Material.Button;
using Google.Android.Material.ProgressIndicator;
using Inbound.App.Updates;

namespace Inbound.App.Screens;

/// <summary>The Settings update card. The activity owns its visible lifecycle.</summary>
internal sealed class AppUpdatesScreen : IDisposable
{
    private readonly MainActivity _activity;
    private readonly AndroidUpdater _service;
    private readonly TextView _status, _offerMetadata, _notesTitle, _notes;
    private readonly View _offerDetails;
    private readonly LinearProgressIndicator _progress;
    private readonly MaterialButton _check, _install;
    private AndroidX.AppCompat.App.AlertDialog? _dialog;
    private OfferKey? _dialogOffer;
    private bool _started, _disposed, _forcePrompt, _installChosen;
    private long? _restoreCode;
    private string? _restoreHash;
    private int _restoreOuterScroll, _restoreNotesScroll;
    private int _generation;

    // Include the displayed text, not only the APK identity: a changed offer must close the old prompt.
    private sealed record OfferKey(long Code, string Hash, string Version, long Size, string? Notes, string? NotesSignature)
    {
        public static OfferKey From(AndroidUpdateManifest m) => new(m.VersionCode, m.Sha256.ToUpperInvariant(), m.VersionName, m.Size, m.ReleaseNotes, m.ReleaseNotesSignature);
    }

    public AppUpdatesScreen(MainActivity activity, View settingsRoot, Bundle? saved = null)
    {
        _activity = activity;
        _service = AndroidUpdates.Service;
        if (saved?.GetBoolean("open") == true && saved.GetLong("code") is > 0 and var code && saved.GetString("hash") is { Length: > 0 } hash)
        {
            _restoreCode = code; _restoreHash = hash;
            _restoreOuterScroll = saved.GetInt("outer_scroll"); _restoreNotesScroll = saved.GetInt("notes_scroll");
        }
        var parent = settingsRoot as LinearLayout ?? throw new ArgumentException("Settings root must be a LinearLayout", nameof(settingsRoot));
        var card = LayoutInflater.From(activity)!.Inflate(Resource.Layout.app_updates, parent, false)!;
        parent.AddView(card);
        _status = card.FindViewById<TextView>(Resource.Id.update_status)!;
        _progress = card.FindViewById<LinearProgressIndicator>(Resource.Id.update_progress)!;
        _check = card.FindViewById<MaterialButton>(Resource.Id.update_check)!;
        _install = card.FindViewById<MaterialButton>(Resource.Id.update_install)!;
        _offerDetails = card.FindViewById<View>(Resource.Id.update_offer_details)!;
        _offerMetadata = card.FindViewById<TextView>(Resource.Id.update_offer_metadata)!;
        _notesTitle = card.FindViewById<TextView>(Resource.Id.update_offer_notes_title)!;
        var notes = UpdateReadyDialog.CreateNotes(activity, out var notesText); _notes = notesText;
        card.FindViewById<FrameLayout>(Resource.Id.update_notes_container)!.AddView(notes, new FrameLayout.LayoutParams(-1, -2));
        var version = activity.PackageManager?.GetPackageInfo(activity.PackageName!, 0)?.VersionName;
        card.FindViewById<TextView>(Resource.Id.update_current_version)!.Text = $"Current version: {version ?? "unknown"}";
        _check.Click += OnCheck;
        _install.Click += OnInstall;
    }

    /// <summary>Remember only a previously open prompt; restoration never starts installation.</summary>
    public void Save(Bundle state)
    {
        CaptureOpenPrompt();
        if (_restoreCode is not { } code || _restoreHash is null) return;
        state.PutBoolean("open", true); state.PutLong("code", code); state.PutString("hash", _restoreHash);
        state.PutInt("outer_scroll", _restoreOuterScroll); state.PutInt("notes_scroll", _restoreNotesScroll);
    }
    private void CaptureOpenPrompt()
    {
        if (_dialog is null || !_dialog.IsShowing || _dialogOffer is null || _installChosen) return;
        _restoreCode = _dialogOffer.Code; _restoreHash = _dialogOffer.Hash;
        (_restoreOuterScroll, _restoreNotesScroll) = UpdateReadyDialog.CaptureScroll(_dialog);
    }
    private void ClearRestore()
    {
        _restoreCode = null; _restoreHash = null; _restoreOuterScroll = _restoreNotesScroll = 0;
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true; _generation++;
        _service.Changed += OnChanged;
        Render(_service.Status);
    }

    public void OpenFromNotification()
    {
        if (_dialog is not null) { _forcePrompt = false; return; }
        _forcePrompt = true;
        if (_started) Render(_service.Status);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false; _generation++;
        _service.Changed -= OnChanged;
        CaptureOpenPrompt();
        _dialog?.Dismiss(); _dialog = null; _dialogOffer = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop(); _disposed = true;
        _check.Click -= OnCheck; _install.Click -= OnInstall;
    }

    private void OnChanged(AndroidUpdateStatus status)
    {
        var generation = _generation;
        _activity.RunOnUiThread(() =>
        {
            if (!_started || _disposed || generation != _generation || _activity.IsFinishing || _activity.IsDestroyed) return;
            Render(_service.Status);
        });
    }

    private void Render(AndroidUpdateStatus status)
    {
        if (!_started || _disposed || _activity.IsFinishing || _activity.IsDestroyed) return;
        _status.Text = status.Text;
        var busy = status.Stage is AndroidUpdateStage.Checking or AndroidUpdateStage.Downloading;
        _check.Enabled = !busy;
        _progress.Visibility = status.Stage == AndroidUpdateStage.Downloading ? ViewStates.Visible : ViewStates.Gone;
        if (status.Stage == AndroidUpdateStage.Downloading)
        {
            _progress.Indeterminate = status.Total <= 0;
            if (status.Total > 0) _progress.Progress = (int)Math.Clamp((double)status.Bytes / status.Total * 1000, 0, 1000);
        }
        var offer = _service.ReadyOffer;
        var key = offer is null ? null : OfferKey.From(offer.Manifest);
        if (_dialog is not null && (_dialogOffer is null || key != _dialogOffer))
        {
            _dialog.Dismiss(); _dialog = null; _dialogOffer = null;
        }
        _install.Visibility = offer is not null ? ViewStates.Visible : ViewStates.Gone;
        _offerDetails.Visibility = offer is not null ? ViewStates.Visible : ViewStates.Gone;
        if (offer is not null)
        {
            var metadata = UpdateReadyDialog.Metadata(offer.Manifest);
            if (_offerMetadata.Text != metadata) _offerMetadata.Text = metadata;
            var title = $"What’s new in {offer.Manifest.VersionName}";
            if (_notesTitle.Text != title) _notesTitle.Text = title;
            UpdateReadyDialog.BindNotes(_notes, offer.Manifest);
        }
        if (_restoreCode is { } restored && _restoreHash is { } restoredHash && offer is not null && _dialog is null)
        {
            int outer = _restoreOuterScroll, notesScroll = _restoreNotesScroll;
            bool sameOffer = offer.Manifest.VersionCode == restored && offer.Manifest.Sha256.Equals(restoredHash, StringComparison.OrdinalIgnoreCase);
            ClearRestore();
            if (sameOffer) ShowOffer(offer.Manifest, outer, notesScroll);
            // A replacement is not the prompt the user had open; do not rebind it during restoration.
            return;
        }
        if (offer is not null && _dialog is null && (_forcePrompt || status.Stage == AndroidUpdateStage.Ready && AndroidUpdates.TryClaimPrompt(offer.Manifest.VersionName)))
        {
            _forcePrompt = false;
            ShowOffer(offer.Manifest);
        }
    }

    private void ShowOffer(AndroidUpdateManifest manifest, int? outerScroll = null, int? notesScroll = null)
    {
        if (!_started || _disposed || _dialog is not null || _activity.IsFinishing || _activity.IsDestroyed) return;
        var selected = OfferKey.From(manifest);
        if (_service.ReadyOffer is not { } ready || OfferKey.From(ready.Manifest) != selected) return;
        var dialog = UpdateReadyDialog.Create(_activity, manifest, () =>
        {
            if (!_started || _disposed || _activity.IsFinishing || _activity.IsDestroyed) return;
            _installChosen = true; ClearRestore();
            var current = _service.ReadyOffer;
            if (current is null || OfferKey.From(current.Manifest) != selected)
            { _activity.ShowMessage("The update offer changed. Review the current update before installing."); return; }
            _ = InstallAsync(selected.Code, selected.Hash);
        });
        _forcePrompt = false; ClearRestore(); _installChosen = false; _dialog = dialog; _dialogOffer = selected;
        dialog.DismissEvent += (_, _) =>
        {
            if (!ReferenceEquals(_dialog, dialog)) return;
            _dialog = null; _dialogOffer = null;
            if (_started) ClearRestore(); // A user dismissal is final; lifecycle Stop deliberately retains it.
        };
        dialog.Show();
        if (outerScroll is not null || notesScroll is not null)
            UpdateReadyDialog.RestoreScroll(dialog, outerScroll ?? 0, notesScroll ?? 0);
    }

    private async void OnCheck(object? sender, EventArgs e)
    {
        try { await AndroidUpdates.CheckNowAsync(); }
        catch (Exception) { if (_started && !_disposed) Toast.MakeText(_activity, "Could not check for updates", ToastLength.Short)?.Show(); }
    }
    private void OnInstall(object? sender, EventArgs e)
    {
        if (_service.ReadyOffer is { } offer) ShowOffer(offer.Manifest);
    }
    private async Task InstallAsync(long versionCode, string hash)
    {
        try { await AndroidUpdates.InstallAsync(_activity, versionCode, hash); }
        catch (Exception) { if (_started && !_disposed) Toast.MakeText(_activity, "Could not open the installer", ToastLength.Short)?.Show(); }
    }
}
