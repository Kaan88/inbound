using Android.Animation;
using Android.App;
using Android.Graphics;
using Android.Views;
using Android.Views.Animations;
using Android.Window;
using Google.Android.Material.BottomSheet;

namespace Inbound.App.Screens;

/// <summary>Moves the sheet independently of the full-window scrim.</summary>
internal sealed class AnimatedSheetDialog(Activity activity, float dim)
    : BottomSheetDialog(activity, Resource.Style.InboundControlledSheetDialog)
{
    private View? _sheet, _scrim;
    private TimeAnimator? _animation;
    private BackCallback? _back;
    private bool _closing, _finishing;

    public override void Show()
    {
        base.Show();
        _sheet = FindViewById(Resource.Id.design_bottom_sheet);
        _scrim = FindViewById(Resource.Id.touch_outside);
        if (_sheet is not { } sheet || _scrim is not { } scrim) return;
        scrim.SetBackgroundColor(Color.Black);
        scrim.Alpha = 0;
        sheet.Alpha = 0;
        sheet.Post(() =>
        {
            if (_closing || !IsShowing) return;
            bool reduced = Motion.Reduced(activity);
            sheet.TranslationY = reduced ? 0 : sheet.Height;
            sheet.Alpha = reduced ? 0 : 1;
            Animate(open: true, reduced);
        });
    }

    public override void OnAttachedToWindow()
    {
        base.OnAttachedToWindow();
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && _back is null && OnBackInvokedDispatcher is { } back)
        {
            // Material also registers Back for its own sheet animation. This dialog owns the transition.
            _back = new BackCallback(Cancel);
            back.RegisterOnBackInvokedCallback(IOnBackInvokedDispatcher.PriorityOverlay, _back);
        }
    }

    public override void OnDetachedFromWindow()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && _back is { } callback)
        {
            OnBackInvokedDispatcher?.UnregisterOnBackInvokedCallback(callback);
            _back = null;
        }
        base.OnDetachedFromWindow();
    }

    [Obsolete("Used by Android versions without the Back dispatcher.")]
    public override void OnBackPressed() => Cancel();

    public override void Dismiss()
    {
        if (_finishing) { base.Dismiss(); return; }
        Close(cancel: false);
    }

    public override void Cancel() => Close(cancel: true);

    private sealed class BackCallback(Action back) : Java.Lang.Object, IOnBackInvokedCallback
    {
        public void OnBackInvoked() => back();
    }

    private void Close(bool cancel)
    {
        if (_closing) return;
        _closing = true;
        if (IsShowing && !activity.IsFinishing && !activity.IsDestroyed && _sheet is { IsShown: true } && _scrim is not null)
            Animate(open: false, Motion.Reduced(activity), () => Finish(cancel));
        else Finish(cancel);
    }

    private void Finish(bool cancel)
    {
        _finishing = true;
        _animation?.Cancel();
        _animation = null;
        // A host switch recreates the activity while the sheet slides away; its window went with the activity, and dismissing it
        // then throws "not attached to window manager" (a crash that hung the app on the S24, 2026-11-22).
        if (activity.IsDestroyed) return;
        if (cancel) base.Cancel();
        else base.Dismiss();
    }

    private void Animate(bool open, bool reduced, Action? complete = null)
    {
        _animation?.Cancel();
        var sheet = _sheet!;
        var scrim = _scrim!;
        float startPosition = sheet.TranslationY, startOpacity = sheet.Alpha, startScrim = scrim.Alpha;
        int panelMs = reduced ? 150 : 400, scrimMs = reduced ? 150 : 200;
        var easing = AnimationUtils.LoadInterpolator(activity, open
            ? Resource.Interpolator.emphasized_decelerate : Resource.Interpolator.emphasized_accelerate);
        var animator = _animation = new TimeAnimator();
        animator.Time += (_, e) =>
        {
            if (activity.IsDestroyed)
            {
                animator.Cancel();
                _animation = null;
                return;
            }

            float panel = Math.Min(1f, e.TotalTime / (float)panelMs);
            float shade = Math.Min(1f, e.TotalTime / (float)scrimMs);
            scrim.Alpha = startScrim + ((open ? dim : 0) - startScrim) * shade;
            if (reduced) sheet.Alpha = startOpacity + ((open ? 1 : 0) - startOpacity) * panel;
            else sheet.TranslationY = startPosition + ((open ? 0 : sheet.Height) - startPosition) * easing!.GetInterpolation(panel);
            if (panel < 1 || shade < 1) return;
            animator.Cancel();
            _animation = null;
            complete?.Invoke();
        };
        animator.Start();
    }
}
