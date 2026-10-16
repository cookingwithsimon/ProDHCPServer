using System.Windows;
using System.Windows.Media;
using ProDHCPServer.Core.Status;

namespace ProDHCPServer.App;

/// <summary>
/// A small latency graph: one point per ping, newest on the right. A missed ping draws a full-height mark in
/// the loss colour and breaks the line, so drop-outs stand out without reading numbers.
/// </summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples), typeof(IReadOnlyList<ProbeResult>), typeof(Sparkline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SlotsProperty = DependencyProperty.Register(
        nameof(Slots), typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineBrushProperty = DependencyProperty.Register(
        nameof(LineBrush), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LossBrushProperty = DependencyProperty.Register(
        nameof(LossBrush), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Red, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BaselineBrushProperty = DependencyProperty.Register(
        nameof(BaselineBrush), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ProbeResult>? Samples { get => (IReadOnlyList<ProbeResult>?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    /// <summary>How many pings fit across the width (one per second, so 60 = the last minute).</summary>
    public int Slots { get => (int)GetValue(SlotsProperty); set => SetValue(SlotsProperty, value); }
    public Brush LineBrush { get => (Brush)GetValue(LineBrushProperty); set => SetValue(LineBrushProperty, value); }
    public Brush LossBrush { get => (Brush)GetValue(LossBrushProperty); set => SetValue(LossBrushProperty, value); }
    public Brush? BaselineBrush { get => (Brush?)GetValue(BaselineBrushProperty); set => SetValue(BaselineBrushProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        if (BaselineBrush is { } baseline) dc.DrawRectangle(baseline, null, new Rect(0, h - 1, w, 1));

        var samples = Samples;
        if (samples is null || samples.Count == 0) return;
        int slots = Math.Max(2, Slots);
        var shown = samples.Skip(Math.Max(0, samples.Count - slots)).ToList();
        double step = w / (slots - 1);
        double x0 = w - (shown.Count - 1) * step;

        // Scale to the slowest reply shown, but never below 10 ms so a quiet 1–2 ms link stays flat.
        double max = Math.Max(10, shown.Where(s => s.Success).Select(s => s.RoundTripMs ?? 0).DefaultIfEmpty(0).Max());
        double pad = 2;
        double Y(double ms) => h - pad - (h - 2 * pad) * Math.Min(ms, max) / max;

        var pen = new Pen(LineBrush, 1.5) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();
        double lossWidth = Math.Clamp(step * 0.6, 1.5, 4);
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            bool open = false;
            for (int i = 0; i < shown.Count; i++)
            {
                double x = x0 + i * step;
                var s = shown[i];
                if (!s.Success || s.RoundTripMs is not { } ms)
                {
                    dc.DrawRectangle(LossBrush, null, new Rect(x - lossWidth / 2, 0, lossWidth, h));
                    open = false;
                    continue;
                }
                var p = new Point(x, Y(ms));
                if (!open) { ctx.BeginFigure(p, false, false); open = true; }
                else ctx.LineTo(p, true, true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);

        // A lone reply between misses has no line segment; give it a dot.
        for (int i = 0; i < shown.Count; i++)
        {
            if (!shown[i].Success || shown[i].RoundTripMs is not { } ms) continue;
            bool before = i > 0 && shown[i - 1].Success;
            bool after = i < shown.Count - 1 && shown[i + 1].Success;
            if (!before && !after) dc.DrawEllipse(LineBrush, null, new Point(x0 + i * step, Y(ms)), 1.5, 1.5);
        }
    }
}
