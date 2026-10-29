using System.Buffers;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using ScottPlot.DataSources;
using ScottPlot.Plottables;
using SkiaSharp;

namespace ScottPlot.D3D12.Rendering.Internal;

/// <summary>Preserves the built-in render action while avoiding redundant sampling, path calls and zero-area markers.</summary>
internal sealed class OptimizedPlottables : IRenderAction, IDisposable
{
    private readonly FrameClock Clock = new();
    private readonly ConditionalWeakTable<double[], ColumnPlan> XPlans = new();
    private readonly ConditionalWeakTable<ISignalXYSource, SourceCache> Sources = new();
    private readonly ConditionalWeakTable<SignalXY, LineCache> Lines = new();
    private static readonly FieldInfo? DoubleXs = typeof(SignalXYSourceDoubleArray).GetField("Xs", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? DoubleYs = typeof(SignalXYSourceDoubleArray).GetField("Ys", BindingFlags.Instance | BindingFlags.NonPublic);

    /// <inheritdoc />
    public void Render(RenderPack rp)
    {
        Clock.Begin();
        IPlottable[] visible = ArrayPool<IPlottable>.Shared.Rent(Math.Max(1, rp.Plot.PlottableList.Count));
        try
        {
            int count = 0;
            foreach (IPlottable item in rp.Plot.PlottableList) if (item.IsVisible) visible[count++] = item;
            for (int i = 0; i < count; i++)
            {
                IPlottable plottable = visible[i];
                plottable.Axes.DataRect = rp.DataRect;
                rp.CanvasState.Save();
                try
                {
                    if (plottable is IPlottableGL) { Clock.Begin(); plottable.Render(rp); Clock.Begin(); continue; }
                    rp.CanvasState.Clip(rp.DataRect);
                    if (plottable.GetType() == typeof(SignalXY) && plottable is SignalXY signal && signal.ConnectStyle == ConnectStyle.Straight)
                        RenderSignal(signal, rp);
                    else { Clock.Begin(); plottable.Render(rp); Clock.Begin(); }
                }
                finally { rp.CanvasState.DisableClipping(); }
            }
        }
        finally { ArrayPool<IPlottable>.Shared.Return(visible, clearArray: true); }
    }
    private void RenderSignal(SignalXY signal, RenderPack rp)
    {
        if (signal.Data.Count == 0) return;
        IReadOnlyList<Pixel> pixels = GetPixels(signal, rp);
        LineCache line = Lines.GetValue(signal, static _ => new());
        if (signal.LineStyle.CanBeRendered && pixels.Count >= 2)
        {
            signal.LineStyle.ApplyToPaint(rp.Paint);
            if (signal.LineStyle.Hairline) rp.Paint.StrokeWidth = 1f / rp.Canvas.TotalMatrix.ScaleX;
            ReadOnlySpan<Pixel> span;
            if (pixels is ReusedPixels reused) span = reused.Span;
            else if (pixels is Pixel[] array) span = array;
            else if (pixels is ArraySegment<Pixel> segment) span = segment.AsSpan();
            else
            {
                if (line.Buffer.Length < pixels.Count) line.Buffer = new Pixel[pixels.Count];
                for (int i = 0; i < pixels.Count; i++) line.Buffer[i] = pixels[i];
                span = line.Buffer.AsSpan(0, pixels.Count);
            }
            BulkPathLine.Rebuild(line.Path, span);
            rp.Canvas.DrawPath(line.Path, rp.Paint.SKPaint);
        }
        if (BulkPathLine.CanSkipZeroMarker(signal.MarkerStyle))
        {
            // Preserve the shared Paint state that the original final zero-area fill would leave behind.
            MarkerStyle marker = signal.MarkerStyle;
            if (pixels.Count != 0 && marker.IsVisible && marker.FillStyle.IsVisible && marker.FillStyle.Color != Colors.Transparent)
                marker.FillStyle.ApplyToPaint(rp.Paint, new PixelRect(pixels[^1], 0));
        }
        else
        {
            Drawing.DrawMarkers(rp.Canvas, rp.Paint, pixels, signal.MarkerStyle);
            if (signal.MarkerStyle.CustomRenderer is not null || signal.MarkerStyle.FillStyle.Hatch is not null) Clock.Begin();
        }
    }

    private IReadOnlyList<Pixel> GetPixels(SignalXY signal, RenderPack rp)
    {
        ISignalXYSource source = signal.Data;
        double[]? xs = null, ys = null;
        if (source.GetType() == typeof(SignalXYSourceDoubleArray) && DoubleXs is not null && DoubleYs is not null)
        {
            xs = DoubleXs.GetValue(source) as double[]; ys = DoubleYs.GetValue(source) as double[];
        }
        else if (source.GetType() == typeof(SignalXYSourceGenericArray<double, double>) && source is SignalXYSourceGenericArray<double, double> arrays)
        {
            xs = arrays.Xs; ys = arrays.Ys;
        }
        // Other sources keep their original CPU data-preparation algorithm and still draw onto this GPU canvas.
        if (xs is null || ys is null)
        {
            Clock.Begin(); var pixels = source.GetPixelsToDraw(rp, signal.Axes, signal.ConnectStyle); Clock.Begin(); return pixels;
        }
        if (!Sources.TryGetValue(source, out SourceCache? cached) || !ReferenceEquals(cached.Xs, xs) || !ReferenceEquals(cached.Ys, ys))
        {
            Sources.Remove(source);
            ColumnPlan plan = XPlans.GetValue(xs, static _ => new());
            cached = new(xs, ys, new FastSignalSource(xs, ys, Clock, plan, Vector256.IsHardwareAccelerated));
            Sources.Add(source, cached);
        }
        FastSignalSource fast = cached.Fast;
        fast.XScale = source.XScale; fast.YScale = source.YScale; fast.XOffset = source.XOffset; fast.YOffset = source.YOffset;
        fast.Rotated = source.Rotated; fast.MinimumIndex = source.MinimumIndex; fast.MaximumIndex = source.MaximumIndex;
        return fast.GetPixelsToDraw(rp, signal.Axes, signal.ConnectStyle);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var entry in Lines) entry.Value.Path.Dispose();
        Lines.Clear(); Sources.Clear(); XPlans.Clear();
    }

    /// <summary>Retains an array adapter without replacing the caller's source or plottable.</summary>
    private sealed record SourceCache(double[] Xs, double[] Ys, FastSignalSource Fast);
    /// <summary>Reuses native path capacity while rebuilding its contents every frame.</summary>
    private sealed class LineCache { internal readonly SKPath Path = new(); internal Pixel[] Buffer = []; }
}

