using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using AndroidX.AppCompat.App;
using DownloadManager.Contracts;
using DownloadManager.Mobile.Remote;
using Google.Android.Material.BottomSheet;
using Google.Android.Material.Button;
using Google.Android.Material.CheckBox;
using Google.Android.Material.TextField;

namespace Inbound.App.Hosts;

/// <summary>
/// "Connect to another Inbound": pairs with a server the way the desktop window does (its address and its password once,
/// <c>POST /api/pair</c> for a <c>remoteClient</c> token, design D9). The password is never kept or logged; the token goes
/// to the Keystore-backed store. Plain http to another machine needs a tick first. On success the new host is shown.
/// </summary>
[Register("app.inbound.android.PairingSheet")]
public sealed class PairingSheet : BottomSheetDialogFragment
{
    public const string SheetTag = "pair";

    private CancellationTokenSource? _pairing;

    public PairingSheet() { }

    private PairingSheet(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    internal static void Show(AppCompatActivity activity)
    {
        if (activity.SupportFragmentManager.FindFragmentByTag(SheetTag) is not null) return;
        new PairingSheet().Show(activity.SupportFragmentManager, SheetTag);
    }

    public override View? OnCreateView(LayoutInflater inflater, ViewGroup? container, Bundle? savedInstanceState)
    {
        var v = inflater.Inflate(Resource.Layout.host_pair, container, false)!;
        var addressLayout = v.FindViewById<TextInputLayout>(Resource.Id.pair_address_layout)!;
        var address = v.FindViewById<TextInputEditText>(Resource.Id.pair_address)!;
        var password = v.FindViewById<TextInputEditText>(Resource.Id.pair_password)!;
        var name = v.FindViewById<TextInputEditText>(Resource.Id.pair_name)!;
        var plainHttp = v.FindViewById<MaterialCheckBox>(Resource.Id.pair_plain_http)!;
        var error = v.FindViewById<TextView>(Resource.Id.pair_error)!;
        var progress = v.FindViewById<View>(Resource.Id.pair_progress)!;
        var connect = v.FindViewById<MaterialButton>(Resource.Id.pair_btn)!;
        var certText = v.FindViewById<TextView>(Resource.Id.pair_cert)!;
        var trustCert = v.FindViewById<MaterialCheckBox>(Resource.Id.pair_trust_cert)!;
        var busy = false;
        // The certificate shown for the address as it was typed then; typing another address forgets it.
        ServerCertificateInfo? certificate = null;
        string? certificateFor = null;

        void Update()
        {
            var text = address.Text?.Trim() ?? "";
            var valid = HostDirectory.CheckAddress(text, out _, out var problem, out var needsConfirmation);
            addressLayout.Error = text.Length > 0 && !valid ? problem : null;
            plainHttp.Visibility = valid && needsConfirmation ? ViewStates.Visible : ViewStates.Gone;
            if (certificate is not null && certificateFor != text) ShowCertificate(null, null);
            connect.Enabled = !busy && valid && !string.IsNullOrEmpty(password.Text) && (!needsConfirmation || plainHttp.Checked)
                && (certificate is null || trustCert.Checked);
        }

        void ShowCertificate(ServerCertificateInfo? seen, string? forAddress)
        {
            certificate = seen;
            certificateFor = forAddress;
            trustCert.Checked = false;
            certText.Text = seen is null ? null
                : $"For: {seen.Subject}\nValid until: {seen.NotAfter:d MMMM yyyy}\nSHA-256: {seen.Fingerprint}";
            certText.Visibility = trustCert.Visibility = seen is null ? ViewStates.Gone : ViewStates.Visible;
        }

        void ShowError(string? words)
        {
            error.Text = words;
            error.Visibility = words is null ? ViewStates.Gone : ViewStates.Visible;
        }

        void Submit()
        {
            if (!connect.Enabled) return;
            busy = true;
            Update();
            ShowError(null);
            progress.Visibility = ViewStates.Visible;
            var request = new PairRequest(address.Text?.Trim() ?? "", password.Text ?? "", name.Text?.Trim(), plainHttp.Checked,
                certificate is not null && trustCert.Checked ? certificate.Fingerprint : null);
            var hosts = HostChoice.Hosts(RequireContext());
            _pairing?.Cancel();
            var cancel = _pairing = new CancellationTokenSource();
            _ = Task.Run(async () =>
            {
                PairOutcome outcome;
                try { outcome = await hosts.PairAsync(request, cancel.Token).ConfigureAwait(false); }
                catch (System.OperationCanceledException) { return; }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    // Only the type: the words of a network error are not shown raw, and a password is never in them.
                    Android.Util.Log.Warn(InboundApplication.Tag, "pairing failed (" + e.GetType().Name + ")");
                    outcome = new PairOutcome(null, "The pairing failed. Check the address and try again.");
                }

                new Handler(Looper.MainLooper!).Post(() =>
                {
                    if (cancel.IsCancellationRequested || !IsAdded) return;
                    busy = false;
                    progress.Visibility = ViewStates.Gone;
                    if (outcome.Host is { } host)
                    {
                        password.Text = "";
                        Dismiss();
                        hosts.Select(host.Id); // every screen now shows it (HostBar recreates them)
                        return;
                    }

                    ShowError(outcome.Error);
                    // An https server whose certificate the phone doesn't trust: show it, and let the user trust exactly that one.
                    if (outcome.NeedsCertificateTrust && outcome.Certificate is { } seen) ShowCertificate(seen, request.AddressText);
                    Update();
                });
            });
        }

        address.TextChanged += (_, _) => Update();
        password.TextChanged += (_, _) => Update();
        plainHttp.CheckedChange += (_, _) => Update();
        trustCert.CheckedChange += (_, _) => Update();
        connect.Click += (_, _) => Submit();
        name.EditorAction += (_, e) =>
        {
            if (e.ActionId == Android.Views.InputMethods.ImeAction.Done) { Submit(); e.Handled = true; }
        };
        Update();
        return v;
    }

    public override void OnDestroyView()
    {
        // A closed sheet stops waiting. A pairing cancelled before the server answered is forgotten there by the client;
        // one that had already finished stays in the host list.
        _pairing?.Cancel();
        _pairing = null;
        base.OnDestroyView();
    }
}
