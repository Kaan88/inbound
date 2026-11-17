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
    private readonly View _column;
    private readonly Handler _ui = new(Looper.MainLooper!);
    private readonly BackCallback _back;
    private Func<View?>? _returnTo;
    private Action? _closed;
    private View? _page;
    private bool _closing;
    private int _generation;

    public Navigator(AppCompatActivity activity, ViewGroup root, FrameLayout overlay, Func<BackStep> plan, Action<BackStep> perform)
    {
        _activity = activity;
        _root = root;
        _overlay = overlay;
        _column = activity.FindViewById(Resource.Id.column)!;
        _back = new BackCallback(this, plan, perform);
        activity.OnBackPressedDispatcher.AddCallback(activity, _back);
    }

    /// <summary>
    /// The row card hidden while its page is open; a recycled holder must keep it hidden until the page has closed. Kept per
    /// activity, so a destroyed activity's card is never held on to (review of steps 3 and 6, finding 16).
    /// </summary>
    public View? HiddenCard { get; private set; }

    /// <summary>A page is open, or still closing (a back while it closes is taken and does nothing).</summary>
    public bool PageOpen => _page is not null;

    public View? Page => _page;

    /// <summary>Call after anything that changes what back would do (selection, search, tab, page): the callback is on only when there is something to undo.</summary>
    public void UpdateBack() => _back.Enabled = _back.Plan() != BackStep.System;

    /// <param name="from">The row card to grow out of, or null.</param>
    /// <param name="returnTo">Finds the card again when the page closes (null: the row is not on screen).</param>
    /// <param name="title">The page's name, announced by TalkBack when it opens.</param>
    public void Open(View page, View? from, Func<View?>? returnTo, Action? closed, string title)
    {
        if (PageOpen) return;
        _generation++;
        _page = page;
        _closing = false;
        _returnTo = returnTo;
        _closed = closed;
        _overlay.RemoveAllViews();
        _overlay.AddView(page, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MatchParent, ViewGroup.LayoutParams.MatchParent));
        // The overlay itself is see-through: the (opaque) page grows over the list, which stays drawn until it is covered (finding 21).
        _overlay.Visibility = ViewStates.Visible;
        // TalkBack and the keyboard stay on the page: the screen under it is hidden from both while it is open (findings 17, 20).
        HideBehind(true);
        AndroidX.Core.View.ViewCompat.SetAccessibilityPaneTitle(page, title);
        AndroidX.Core.View.ViewCompat.RequestApplyInsets(page);
        page.Visibility = ViewStates.Invisible;
        UpdateBack();
        if (Motion.Reduced(_activity))
        {
            Motion.CrossFade(_column, page, 150);
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
    }

    /// <summary>
    /// Closes <paramref name="page"/> when it is the open one. A late call for a page that has gone (a slow add, a removal seen
    /// after back), or a second call while it closes, does nothing (findings 18, 22).
    /// </summary>
    public void Close(View page)
    {
        if (!ReferenceEquals(page, _page) || _closing) return;
        _closing = true;
        page.Alpha = 1f; page.ScaleX = 1f; page.ScaleY = 1f; page.TranslationX = 0f;
        var card = _returnTo?.Invoke();
        var closed = _closed;
        _closed = null;
        var generation = _generation;
        void Finish()
        {
            if (generation != _generation || !ReferenceEquals(page, _page)) return;
            Gone(closed);
        }

        if (Motion.Reduced(_activity))
        {
            if (HiddenCard is { } h0) h0.Visibility = ViewStates.Visible;
            HiddenCard = null;
            // The overlay is see-through, so this is a true cross-fade: the page out, the list in.
            _column.Alpha = 0f;
            Motion.CrossFade(page, _column, 150, Finish);
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

    /// <summary>The activity is going away: the page goes at once (its closed callback runs) and nothing is held on to.</summary>
    public void Dispose()
    {
        _ui.RemoveCallbacksAndMessages(null);
        var closed = _closed;
        _closed = null;
        if (_page is not null) Gone(closed);
        HiddenCard = null;
    }

    private void Gone(Action? closed)
    {
        _generation++;
        _page = null;
        _closing = false;
        _returnTo = null;
        _overlay.RemoveAllViews();
        _overlay.Visibility = ViewStates.Gone;
        _column.Alpha = 1f;
        HideBehind(false);
        if (HiddenCard is { } h) { h.Visibility = ViewStates.Visible; HiddenCard = null; }
        UpdateBack();
        closed?.Invoke();
    }

    private void HideBehind(bool hide)
    {
        _column.ImportantForAccessibility = hide ? ImportantForAccessibility.NoHideDescendants : ImportantForAccessibility.Auto;
        if (_column is ViewGroup g) g.DescendantFocusability = hide ? DescendantFocusability.BlockDescendants : DescendantFocusability.BeforeDescendants;
    }

    private sealed class BackCallback(Navigator owner, Func<BackStep> plan, Action<BackStep> perform) : OnBackPressedCallback(false)
    {
        public Func<BackStep> Plan => plan;

        public override void HandleOnBackPressed()
        {
            var step = plan();
            if (step == BackStep.ClosePage) { if (owner._page is { } page) owner.Close(page); }
            else perform(step);
        }

        // Predictive back: the page shrinks a little and slides with the finger; cancelled, it springs back.
        public override void HandleOnBackProgressed(BackEventCompat backEvent)
        {
            if (owner.Page is not { } p || owner._closing) return;
            var progress = backEvent.Progress;
            var dir = backEvent.SwipeEdge == BackEventCompat.EdgeLeft ? 1f : -1f;
            p.ScaleX = p.ScaleY = 1f - 0.1f * progress;
            p.TranslationX = dir * progress * Motion.Dp(owner._activity, 24);
        }

        public override void HandleOnBackCancelled()
        {
            if (owner.Page is not { } p || owner._closing) return;
            p.Animate()!.ScaleX(1f).ScaleY(1f).TranslationX(0f).SetDuration(150).Start();
        }
    }
}
