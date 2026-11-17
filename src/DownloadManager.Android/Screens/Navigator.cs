using Android.OS;
using Android.Views;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.AppCompat.App;
using AndroidX.Transitions;
using DownloadManager.Mobile;
using Google.Android.Material.Transition;

namespace Inbound.App.Screens;

/// <summary>
/// One page at a time over the Downloads screen (Details, New download). A page grows out of the row it was opened from (container
/// transform, 450 ms) and goes back into it; one opened from nowhere fades through. With "Remove animations" on, both are a plain
/// cross-fade. Also the back gesture: a page follows the finger a little (predictive back) and the app steps in only while it has
/// something to undo, so with nothing open the system's own back-to-home preview is left alone.
/// </summary>
internal sealed class Navigator
{
    private readonly AppCompatActivity _activity;
    private readonly ViewGroup _root;
    private readonly FrameLayout _overlay;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly BackCallback _back;
    private Func<View?>? _returnTo;
    private Action? _closed;

    /// <summary>The row card hidden while its page is open; a recycled holder must keep it hidden until the page has closed.</summary>
    public static View? HiddenCard { get; private set; }

    public Navigator(AppCompatActivity activity, ViewGroup root, FrameLayout overlay, Func<BackStep> plan, Action<BackStep> perform)
    {
        _activity = activity;
        _root = root;
        _overlay = overlay;
        _back = new BackCallback(this, plan, perform);
        activity.OnBackPressedDispatcher.AddCallback(activity, _back);
    }

    public bool PageOpen => _overlay.ChildCount > 0;

    public View? Page => PageOpen ? _overlay.GetChildAt(0) : null;

    /// <summary>Call after anything that changes what back would do (selection, search, tab, page): the callback is on only when there is something to undo.</summary>
    public void UpdateBack() => _back.Enabled = _back.Plan() != BackStep.System;

    /// <param name="from">The row card to grow out of, or null.</param>
    /// <param name="returnTo">Finds the card again when the page closes (null: the row is not on screen).</param>
    public void Open(View page, View? from, Func<View?>? returnTo, Action? closed = null)
    {
        if (PageOpen) return;
        _returnTo = returnTo;
        _closed = closed;
        _overlay.RemoveAllViews();
        _overlay.AddView(page, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        _overlay.Visibility = ViewStates.Visible;
        AndroidX.Core.View.ViewCompat.RequestApplyInsets(page);
        page.Visibility = ViewStates.Invisible;
        if (Motion.Reduced(_activity))
        {
            Motion.CrossFade(_activity.FindViewById(Resource.Id.column)!, page, 150);
            UpdateBack();
            return;
        }

        if (from is not null)
        {
            var t = new MaterialContainerTransform { StartView = from, EndView = page, ScrimColor = Android.Graphics.Color.Transparent };
            t.SetDuration(Motion.ContainerMs); t.AddTarget(page);
            HiddenCard = from;
            TransitionManager.BeginDelayedTransition(_root, t);
            from.Visibility = ViewStates.Invisible;
        }
        else TransitionManager.BeginDelayedTransition(_root, Motion.Fade());
        page.Visibility = ViewStates.Visible;
        UpdateBack();
    }

    public void Close()
    {
        var page = Page;
        if (page is null) return;
        page.Alpha = 1f; page.ScaleX = 1f; page.ScaleY = 1f; page.TranslationX = 0f;
        var card = _returnTo?.Invoke();
        var closed = _closed;
        _closed = null;
        void Finish()
        {
            _overlay.RemoveAllViews();
            _overlay.Visibility = ViewStates.Gone;
            if (HiddenCard is { } h) { h.Visibility = ViewStates.Visible; HiddenCard = null; }
            UpdateBack();
            closed?.Invoke();
        }

        if (Motion.Reduced(_activity))
        {
            var column = _activity.FindViewById(Resource.Id.column)!;
            if (HiddenCard is { } h0) h0.Visibility = ViewStates.Visible;
            HiddenCard = null;
            column.Alpha = 0f;
            Motion.CrossFade(page, column, 150, Finish);
            return;
        }

        if (card is not null && card.IsAttachedToWindow)
        {
            var t = new MaterialContainerTransform { StartView = page, EndView = card, ScrimColor = Android.Graphics.Color.Transparent };
            t.SetDuration(Motion.ContainerMs); t.AddTarget(card);
            TransitionManager.BeginDelayedTransition(_root, t);
            card.Visibility = ViewStates.Visible;
            page.Visibility = ViewStates.Invisible;
            _ui.PostDelayed(Finish, Motion.ContainerMs + 50);
        }
        else
        {
            TransitionManager.BeginDelayedTransition(_root, Motion.Fade());
            page.Visibility = ViewStates.Invisible;
            _ui.PostDelayed(Finish, Motion.NavigationMs + 50);
        }
    }

    private sealed class BackCallback(Navigator owner, Func<BackStep> plan, Action<BackStep> perform) : OnBackPressedCallback(false)
    {
        public Func<BackStep> Plan => plan;

        public override void HandleOnBackPressed()
        {
            var step = plan();
            if (step == BackStep.ClosePage) owner.Close();
            else perform(step);
        }

        // Predictive back: the page shrinks a little and slides with the finger; cancelled, it springs back.
        public override void HandleOnBackProgressed(BackEventCompat backEvent)
        {
            if (owner.Page is not { } p) return;
            var progress = backEvent.Progress;
            var dir = backEvent.SwipeEdge == BackEventCompat.EdgeLeft ? 1f : -1f;
            p.ScaleX = p.ScaleY = 1f - 0.1f * progress;
            p.TranslationX = dir * progress * Motion.Dp(owner._activity, 24);
        }

        public override void HandleOnBackCancelled()
        {
            if (owner.Page is not { } p) return;
            p.Animate()!.ScaleX(1f).ScaleY(1f).TranslationX(0f).SetDuration(150).Start();
        }
    }
}
