using Android.Content;
using Android.Graphics;
using Android.Graphics.Drawables;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Android.Widget;

namespace Inbound.App.Screens;

/// <summary>Native radio button with a checkmark aligned to the start of its centered label.</summary>
[Register("app.inbound.android.CaughtTimingRadioButton")]
internal sealed class CaughtTimingRadioButton : RadioButton
{
    private readonly Drawable? _check;
    private readonly int _checkSize, _gap, _labelPadding;

    public CaughtTimingRadioButton(Context context, IAttributeSet? attrs) : base(context, attrs)
    {
        _checkSize = Motion.Dp(context, 16);
        _gap = Motion.Dp(context, 6);
        _labelPadding = PaddingStart;
        _check = context.GetDrawable(Resource.Drawable.ic_check)?.Mutate();
        _check?.SetTint(new Color(Motion.Attr(context, Resource.Attribute.colorOnSecondaryContainer)));
        CheckedChange += (_, _) => UpdateLabelPadding();
        UpdateLabelPadding();
    }

    private CaughtTimingRadioButton(IntPtr handle, JniHandleOwnership transfer) : base(handle, transfer) { }

    private void UpdateLabelPadding()
    {
        int checkSpace = Checked ? _checkSize + _gap : 0;
        SetPaddingRelative(_labelPadding + checkSpace, PaddingTop, PaddingEnd, PaddingBottom);
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        if (!Checked || _check is null || Layout is not { LineCount: > 0 } label) return;

        bool rtl = LayoutDirection == Android.Views.LayoutDirection.Rtl;
        float leadingEdge = rtl ? label.GetLineRight(0) : label.GetLineLeft(0);
        for (int line = 1; line < label.LineCount; line++)
            leadingEdge = rtl ? Math.Max(leadingEdge, label.GetLineRight(line))
                : Math.Min(leadingEdge, label.GetLineLeft(line));

        int left = TotalPaddingLeft + (int)Math.Round(leadingEdge) + (rtl ? _gap : -_gap - _checkSize);
        int top = (Height - _checkSize) / 2;
        _check.SetBounds(left, top, left + _checkSize, top + _checkSize);
        _check.Draw(canvas);
    }
}
