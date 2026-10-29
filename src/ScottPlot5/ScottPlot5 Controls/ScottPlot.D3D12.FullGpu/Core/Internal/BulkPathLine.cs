using System.Diagnostics;
using System.Runtime.InteropServices;
using ScottPlot;
using ScottPlot.Plottables;
using SkiaSharp;

namespace ScottPlot.D3D12.Rendering.Internal;

/// <summary>Rebuilds the same straight path every frame through bulk AddPoly, preserving NaN-separated contours.</summary>
/// <remarks>This is a probe-only wrapper, not a change to SignalXY or a replacement for the full backend.</remarks>
internal sealed class BulkPathLine(SignalXY source, bool skipZeroMarker = false) : IPlottable, IDisposable
{
    private readonly SKPath Path = new();
    internal long PathTicks, DrawTicks; internal bool MeasureDrawCalls = false;
    /// <inheritdoc />
    public bool IsVisible { get => source.IsVisible; set => source.IsVisible = value; }
    /// <inheritdoc />
    public IAxes Axes { get => source.Axes; set => source.Axes = value; }
    /// <inheritdoc />
    public IEnumerable<LegendItem> LegendItems => source.LegendItems;
    /// <inheritdoc />
    public AxisLimits GetAxisLimits() => source.GetAxisLimits();
    /// <inheritdoc />
    public void Render(RenderPack rp)
    {
        if (!source.IsVisible || source.Data.Count == 0) return;
        if (source.ConnectStyle != ConnectStyle.Straight || source.Data is not FastSignalSource fast)
        {
            source.Render(rp);
            return;
        }
        var pixels = (ReusedPixels)fast.GetPixelsToDraw(rp, Axes, source.ConnectStyle);
        if (source.LineStyle.CanBeRendered && pixels.Count >= 2)
        {
            source.LineStyle.ApplyToPaint(rp.Paint);
            if (source.LineStyle.Hairline) rp.Paint.StrokeWidth = 1f / rp.Canvas.TotalMatrix.ScaleX;
            long start = Stopwatch.GetTimestamp();
            Rebuild(Path, pixels.Span);
            PathTicks += Stopwatch.GetTimestamp() - start;
            long drawStart = MeasureDrawCalls ? Stopwatch.GetTimestamp() : 0;
            rp.Canvas.DrawPath(Path, rp.Paint.SKPaint);
            if (MeasureDrawCalls) DrawTicks += Stopwatch.GetTimestamp() - drawStart;
        }
        if (!skipZeroMarker || !CanSkipZeroMarker(source.MarkerStyle))
            Drawing.DrawMarkers(rp.Canvas, rp.Paint, pixels, source.MarkerStyle);
    }

    // The default SignalXY marker is a filled circle with diameter zero and no outline.
    // Keep all visible, custom, hatched, and outlined cases on the original marker path.
    internal static bool CanSkipZeroMarker(MarkerStyle marker) =>
        marker.Size == 0 && marker.Shape == MarkerShape.FilledCircle && marker.CustomRenderer is null &&
        marker.FillStyle.Hatch is null && !marker.OutlineStyle.CanBeRendered;

    internal static void Rebuild(SKPath path, ReadOnlySpan<Pixel> pixels)
    {
        path.Rewind();
        int first = 0;
        for (int i = 0; i <= pixels.Length; i++)
        {
            bool end = i == pixels.Length || float.IsNaN(pixels[i].X) || float.IsNaN(pixels[i].Y);
            if (!end) continue;
            if (i > first) path.AddPoly(MemoryMarshal.Cast<Pixel, SKPoint>(pixels[first..i]), false);
            first = i + 1;
        }
    }

    /// <inheritdoc />
    public void Dispose() => Path.Dispose();
}





