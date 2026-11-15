using Android.Content;
using Android.Graphics;
using Android.Runtime;
using Android.Util;
using Android.Views;
using Google.Android.Material.Color;
using Path = Android.Graphics.Path;

namespace AndroidTrial.Native;

/// <summary>A filled line chart of recent transfer rates, drawn on Canvas. Colours come from the theme (so dark/light/dynamic follow).</summary>
[Register("app.inbound.tk.nativeui.SpeedGraphView")]
public sealed class SpeedGraphView : View
{
    private readonly Paint _line = new(PaintFlags.AntiAlias) { StrokeJoin = Paint.Join.Round };
    private readonly Paint _fill = new(PaintFlags.AntiAlias);
    private readonly Paint _grid = new();
    private readonly Paint _text = new(PaintFlags.AntiAlias);
    private readonly Path _linePath = new();
    private readonly Path _fillPath = new();
    private float[] _samples = [];
    private int _lineColor;

    public SpeedGraphView(Context context, IAttributeSet? attrs) : base(context, attrs)
    {
        var d = Resources!.DisplayMetrics!.Density;
        _line.StrokeWidth = 2.5f * d;
        _text.TextSize = 12f * d;
        _grid.StrokeWidth = Math.Max(1f, d * 0.75f);
        _lineColor = MaterialColors.GetColor(this, Resource.Attribute.colorPrimary);
        _line.SetStyle(Paint.Style.Stroke);
        _fill.SetStyle(Paint.Style.Fill);
        _grid.Color = new Color(MaterialColors.GetColor(this, Resource.Attribute.colorOutline));
        _text.Color = new Color(MaterialColors.GetColor(this, Resource.Attribute.colorOnSurfaceVariant));
        _line.Color = new Color(_lineColor);
    }

    public void SetSamples(IReadOnlyList<float> samples)
    {
        _samples = samples.ToArray();
        Invalidate();
    }

    protected override void OnSizeChanged(int w, int h, int oldw, int oldh)
    {
        base.OnSizeChanged(w, h, oldw, oldh);
        var top = new Color(_lineColor) { A = 110 };
        var bottom = new Color(_lineColor) { A = 6 };
        _fill.SetShader(new LinearGradient(0, 0, 0, h, top, bottom, Shader.TileMode.Clamp!));
    }

    protected override void OnDraw(Canvas canvas)
    {
        base.OnDraw(canvas);
        float w = Width, h = Height, pad = _text.TextSize + 6;
        var plotTop = pad;
        var plotH = h - pad - 2;
        for (var i = 0; i <= 2; i++)
        {
            var y = plotTop + plotH * i / 2f;
            canvas.DrawLine(0, y, w, y, _grid);
        }

        if (_samples.Length < 2) { canvas.DrawText("Waiting for data", 0, h / 2f, _text); return; }
        var max = Math.Max(_samples.Max() * 1.15f, 1f);
        canvas.DrawText(Fmt.Rate(max), 0, _text.TextSize, _text);
        _linePath.Reset();
        _fillPath.Reset();
        var step = w / (JobRow.MaxRates - 1);
        var x0 = w - step * (_samples.Length - 1); // newest sample at the right edge
        for (var i = 0; i < _samples.Length; i++)
        {
            var x = x0 + step * i;
            var y = plotTop + plotH * (1 - _samples[i] / max);
            if (i == 0) { _linePath.MoveTo(x, y); _fillPath.MoveTo(x, plotTop + plotH); _fillPath.LineTo(x, y); }
            else { _linePath.LineTo(x, y); _fillPath.LineTo(x, y); }
        }

        _fillPath.LineTo(w, plotTop + plotH);
        _fillPath.Close();
        canvas.DrawPath(_fillPath, _fill);
        canvas.DrawPath(_linePath, _line);
    }
}
