using Android.Content;
using Android.Views;
using Android.Widget;
using DownloadManager.Mobile.Updates;
using Google.Android.Material.Button;
using Google.Android.Material.Dialog;
using Google.Android.Material.ProgressIndicator;
using Inbound.App.Updates;

namespace Inbound.App.Screens;

/// <summary>The Settings update card. The activity owns its visible lifecycle.</summary>
internal sealed class AppUpdatesScreen : IDisposable
{
    private readonly MainActivity _activity;
    private readonly AndroidUpdater _service;
    private readonly TextView _status;
    private readonly LinearProgressIndicator _progress;
    private readonly MaterialButton _check, _install;
    private Android.App.Dialog? _dialog;
    private bool _started, _disposed, _forcePrompt;
    private int _generation;

    public AppUpdatesScreen(MainActivity activity, View settingsRoot)
    {
        _activity = activity;
        _service = AndroidUpdates.Service;
        var parent = settingsRoot as LinearLayout ?? throw new ArgumentException("Settings root must be a LinearLayout", nameof(settingsRoot));
        var card = LayoutInflater.From(activity)!.Inflate(Resource.Layout.app_updates, parent, false)!;
        parent.AddView(card);
        _status = card.FindViewById<TextView>(Resource.Id.update_status)!;
        _progress = card.FindViewById<LinearProgressIndicator>(Resource.Id.update_progress)!;
        _check = card.FindViewById<MaterialButton>(Resource.Id.update_check)!;
        _install = card.FindViewById<MaterialButton>(Resource.Id.update_install)!;
        var version = activity.PackageManager?.GetPackageInfo(activity.PackageName!, 0)?.VersionName;
        card.FindViewById<TextView>(Resource.Id.update_current_version)!.Text = $"Current version: {version ?? "unknown"}";
        _check.Click += OnCheck;
        _install.Click += OnInstall;
    }

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;
        _generation++;
        _service.Changed += OnChanged;
        Render(_service.Status);
    }

    public void OpenFromNotification()
    {
        _forcePrompt = true;
        if (_started) Render(_service.Status);
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _generation++;
        _service.Changed -= OnChanged;
        _dialog?.Dismiss();
        _dialog = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        Stop();
        _disposed = true;
        _check.Click -= OnCheck;
        _install.Click -= OnInstall;
    }

    private void OnChanged(AndroidUpdateStatus status)
    {
        var generation = _generation;
        _activity.RunOnUiThread(() =>
        {
            if (!_started || _disposed || generation != _generation || _activity.IsFinishing || _activity.IsDestroyed) return;
            Render(status);
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
        _install.Visibility = _service.ReadyOffer is not null ? ViewStates.Visible : ViewStates.Gone;
        var offer = _service.ReadyOffer;
        if (offer is not null && _dialog is null && (_forcePrompt || status.Stage == AndroidUpdateStage.Ready && AndroidUpdates.TryClaimPrompt(offer.Manifest.VersionName)))
        {
            _forcePrompt = false;
            _dialog = new MaterialAlertDialogBuilder(_activity).SetTitle($"Inbound {offer.Manifest.VersionName} is ready")!
                .SetPositiveButton("Install", (_, _) => { _dialog = null; if (_started && !_disposed) _ = AndroidUpdates.InstallAsync(_activity, offer.Manifest.VersionCode, offer.Manifest.Sha256); })!
                .SetNegativeButton("Later", (_, _) => _dialog = null)!
                .SetOnCancelListener(new CancelListener(() => _dialog = null))!.Show();
        }
    }

    private async void OnCheck(object? sender, EventArgs e)
    {
        try { await AndroidUpdates.CheckNowAsync(); }
        catch (Exception) { if (_started && !_disposed) Toast.MakeText(_activity, "Could not check for updates", ToastLength.Short)?.Show(); }
    }
    private void OnInstall(object? sender, EventArgs e) => _ = InstallAsync();
    private async Task InstallAsync()
    {
        try { await AndroidUpdates.InstallAsync(_activity); }
        catch (Exception) { if (_started && !_disposed) Toast.MakeText(_activity, "Could not open the installer", ToastLength.Short)?.Show(); }
    }

    private sealed class CancelListener(Action cancel) : Java.Lang.Object, IDialogInterfaceOnCancelListener
    {
        public void OnCancel(IDialogInterface? dialog) => cancel();
    }
}
