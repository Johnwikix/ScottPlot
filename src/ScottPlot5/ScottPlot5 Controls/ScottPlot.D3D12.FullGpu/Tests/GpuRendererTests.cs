using System.Threading;
using NUnit.Framework;
using ScottPlot.D3D12.Rendering;
using ScottPlot.Plottables;
using SkiaSharp;

namespace ScottPlot.D3D12.Tests;

/// <summary>Verifies real hardware rendering, source/API equivalence and resource ownership without a window.</summary>
[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class GpuRendererTests
{
    /// <summary>Proves the final resolved texture contains a complete Figure and no automatic readback occurred.</summary>
    [Test]
    public void FullFigureIsHardwareBackedAndReadableOnlyOnExplicitCapture()
    {
        using Plot plot = Scene();
        using D3D12Renderer renderer = new();
        renderer.Render(plot, 800, 600);
        Assert.That(renderer.Device.Context.Backend, Is.EqualTo(GRBackend.Direct3D));
        Assert.That(renderer.Device.MaximumBgraSampleCount, Is.GreaterThanOrEqualTo(4));
        Assert.That(renderer.ResolvedTexture.Description.SampleDescription.Count, Is.EqualTo(1));
        Assert.That(renderer.PixelReadbackCount, Is.Zero);
        using SKBitmap bitmap = renderer.CaptureBitmap();
        byte[] pixels = bitmap.Bytes;
        int colored = 0;
        for (int i = 0; i < pixels.Length; i += 4)
            if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) - Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2])) > 30) colored++;
        Assert.That(colored, Is.GreaterThan(500));
        Assert.That(renderer.PixelReadbackCount, Is.EqualTo(1));
        renderer.Render(plot, 800, 600);
        Assert.That(renderer.PixelReadbackCount, Is.EqualTo(1));
        D3D12FrameStatistics frame = renderer.LastFrame;
        Assert.That(frame.TotalMilliseconds, Is.EqualTo(frame.RenderMilliseconds + frame.SubmitMilliseconds + frame.FenceMilliseconds).Within(.001));
    }

    /// <summary>Checks native queue completion using alternating colors without snapshot side effects.</summary>
    [Test]
    public void ActualFenceCompletionMakesTheLatestFrameImmediatelyReadable()
    {
        using Plot plot = new();
        using D3D12Renderer renderer = new();
        for (int i = 0; i < 12; i++)
        {
            Color color = i % 2 == 0 ? Colors.Red : Colors.Blue;
            plot.FigureBackground.Color = color;
            renderer.Render(plot, 320, 240);
            using SKBitmap bitmap = renderer.CaptureBitmap();
            SKColor corner = bitmap.GetPixel(1, 1);
            Assert.That(corner.Red, Is.EqualTo(color.R));
            Assert.That(corner.Blue, Is.EqualTo(color.B));
            Assert.That(corner.Alpha, Is.EqualTo(255));
        }
    }

    /// <summary>Uses the physical viewport for layout even when the retained GPU texture is larger.</summary>
    [TestCase(.5, false), TestCase(1, false), TestCase(1.5, true), TestCase(2, false), TestCase(2, true)]
    public void Test_FullFigure_LayoutMatchesPhysicalViewport(double scale, bool hairline)
    {
        const int width = 800, height = 600;
        using Plot plot = Scene();
        plot.ScaleFactor = scale;
        plot.Axes.Hairline(hairline);
        using SKSurface cpu = SKSurface.Create(new SKImageInfo(width, height));
        plot.Render(cpu.Canvas, width, height);
        PixelRect expectedDataRect = plot.RenderManager.LastRender.DataRect;
        using D3D12Renderer renderer = new();
        renderer.Render(plot, width * 2, height * 2);
        for (int i = 0; i < 3; i++)
        {
            renderer.Render(plot, width, height);
            Assert.Multiple(() =>
            {
                Assert.That(plot.RenderManager.LastRender.FigureRect, Is.EqualTo(new PixelRect(width, height)));
                Assert.That(plot.RenderManager.LastRender.DataRect, Is.EqualTo(expectedDataRect));
                Assert.That(plot.ScaleFactor, Is.EqualTo(scale));
            });
        }
        Assert.That(renderer.PixelReadbackCount, Is.Zero);
    }

    /// <summary>Compares original and optimized native GPU paths while keeping public plottable/data references unchanged.</summary>
    [TestCase(ConnectStyle.Straight), TestCase(ConnectStyle.StepHorizontal), TestCase(ConnectStyle.StepVertical)]
    public void OptimizedSignalKeepsPublicApiAndPixels(ConnectStyle style)
    {
        using Plot plot = Scene();
        SignalXY signal = plot.PlottableList.OfType<SignalXY>().First();
        signal.ConnectStyle = style;
        ISignalXYSource source = signal.Data;
        IPlottable[] objects = plot.PlottableList.ToArray();
        IRenderAction[] actions = plot.RenderManager.RenderActions.ToArray();
        AssertEquivalent(plot, "connect-" + style);
        Assert.That(signal.Data, Is.SameAs(source));
        Assert.That(plot.PlottableList, Is.EqualTo(objects));
        Assert.That(plot.RenderManager.RenderActions, Is.EqualTo(actions));
    }

    /// <summary>Includes NaNs, extrema, axis reversals, rotation and negative Y scaling.</summary>
    [TestCase(false, false), TestCase(false, true), TestCase(true, false), TestCase(true, true)]
    public void SamplingExceptionalDataKeepsNativeGpuOutput(bool rotated, bool reversed)
    {
        using Plot plot = new();
        double[] xs = Enumerable.Range(0, 20000).Select(i => i * .01).ToArray();
        double[] ys = xs.Select(x => Math.Sin(x) + .2 * Math.Cos(x * 53)).ToArray();
        for (int i = 41; i < ys.Length; i += 271) ys[i] = double.NaN;
        ys[801] = 30; ys[802] = -30; ys[900] = -0.0;
        SignalXY signal = plot.Add.SignalXY(xs, ys);
        signal.Data.Rotated = rotated; signal.Data.YScale = -1.7; signal.Data.YOffset = .3;
        signal.LineWidth = 2; signal.LineStyle.AntiAlias = true;
        if (rotated) plot.Axes.SetLimits(-3, 3, reversed ? 175 : 15, reversed ? 15 : 175);
        else plot.Axes.SetLimits(reversed ? 175 : 15, reversed ? 15 : 175, -3, 3);
        AssertEquivalent(plot, $"special-{rotated}-{reversed}");
    }

    /// <summary>Recomputes a shared X plan for every frame even when array counts do not change.</summary>
    [Test]
    public void InPlaceMutationsAreNotMistakenForCachedGeometry()
    {
        using Plot plot = new();
        double[] xs = Enumerable.Range(0, 20000).Select(i => i * .01).ToArray();
        double[] a = xs.Select(Math.Sin).ToArray(), b = xs.Select(x => Math.Cos(x)).ToArray();
        plot.Add.SignalXY(xs, a); plot.Add.SignalXY(xs, b); plot.Axes.SetLimits(-5, 210, -2, 2);
        using D3D12Renderer renderer = new();
        renderer.Render(plot, 800, 600);
        using SKBitmap before = renderer.CaptureBitmap();
        a[10000] = 40; b[10001] = -40;
        for (int i = 0; i < xs.Length; i++) xs[i] *= .99;
        renderer.Render(plot, 800, 600);
        using SKBitmap after = renderer.CaptureBitmap();
        Assert.That(Difference(before.Bytes, after.Bytes), Is.GreaterThan(10));
        AssertEquivalent(plot, "in-place-updated");
    }

    /// <summary>Preserves visible markers, outlines and custom renderer calls when zero markers are optimized.</summary>
    [TestCase("circle"), TestCase("square"), TestCase("outline"), TestCase("hairline"), TestCase("custom")]
    public void MarkerGuardsKeepTheirVisualAndCustomSemantics(string mode)
    {
        using Plot plot = Scene();
        SignalXY signal = plot.PlottableList.OfType<SignalXY>().First();
        CountingMarker? custom = null;
        switch (mode)
        {
            case "circle": signal.MarkerSize = 4; break;
            case "square": signal.MarkerSize = 4; signal.MarkerShape = MarkerShape.FilledSquare; break;
            case "outline": signal.MarkerStyle.OutlineWidth = 1; break;
            case "hairline": signal.MarkerStyle.OutlineStyle.Hairline = true; break;
            case "custom": signal.MarkerStyle.CustomRenderer = custom = new(); break;
        }
        AssertEquivalent(plot, "marker-" + mode);
        if (custom is not null) Assert.That(custom.Calls, Is.GreaterThan(0));
    }

    /// <summary>Renders all subplots at non-unit scale without accumulating the previous subplot's matrix.</summary>
    [Test]
    public void MultiplotRetainsPhysicalLayoutAtHighDpi()
    {
        using Plot first = Scene(); using Plot second = Scene();
        first.ScaleFactor = second.ScaleFactor = 1.5;
        Multiplot plots = new(first); plots.AddPlot(second);
        using D3D12Renderer renderer = new(); renderer.Render(plots, 1000, 800);
        Assert.That(first.RenderManager.LastRender.DataRect.HasArea, Is.True);
        Assert.That(second.RenderManager.LastRender.DataRect.HasArea, Is.True);
        Assert.That(first.RenderManager.LastRender.DataRect.Bottom, Is.LessThan(second.RenderManager.LastRender.DataRect.Top));
        using SKBitmap bitmap = renderer.CaptureBitmap();
        Assert.That(bitmap.Width, Is.EqualTo(1000)); Assert.That(bitmap.Height, Is.EqualTo(800));
    }

    /// <summary>Reuses one target capacity across repeated identical resize cycles.</summary>
    [Test]
    public void ResizeTargetCapacityIsBoundedAcrossRepeatedCycles()
    {
        using Plot plot = Scene(); using D3D12Renderer renderer = new();
        int afterFirst = 0;
        for (int cycle = 0; cycle < 4; cycle++)
        {
            for (int i = 0; i < 12; i++) renderer.Render(plot, 400 + i * 31, 300 + i * 19);
            if (cycle == 0) afterFirst = renderer.LastFrame.TargetAllocations;
            else Assert.That(renderer.LastFrame.TargetAllocations, Is.EqualTo(afterFirst));
        }
        Assert.That(renderer.PixelReadbackCount, Is.Zero);
    }

    /// <summary>Shared device leases keep another live renderer valid after the first is disposed.</summary>
    [Test]
    public void DeviceLeasesAreSharedAndReleasedIndependently()
    {
        using Plot plot = Scene(); using D3D12Renderer second = new();
        using (D3D12Renderer first = new())
        {
            Assert.That(first.Device, Is.SameAs(second.Device));
            first.Render(plot, 400, 300);
            Assert.Throws<InvalidOperationException>(() => second.Device.Dispose());
        }
        second.Render(plot, 400, 300);
        using SKBitmap bitmap = second.CaptureBitmap();
        Assert.That(bitmap.Width, Is.EqualTo(400));
    }

    /// <summary>Rendering cannot silently migrate to a worker thread or continue after disposal.</summary>
    [Test]
    public void InvalidThreadAndDisposedUseFailExplicitly()
    {
        using Plot plot = Scene();
        D3D12Renderer renderer = new();
        Assert.Throws<InvalidOperationException>(() => Task.Run(() => renderer.Render(plot, 320, 240)).GetAwaiter().GetResult());
        renderer.Dispose();
        Assert.Throws<ObjectDisposedException>(() => renderer.Render(plot, 320, 240));
    }

    /// <summary>Transparent Figure pixels remain transparent after native MSAA resolve.</summary>
    [Test]
    public void TransparentBackgroundIsPreserved()
    {
        using Plot plot = Scene(); plot.FigureBackground.Color = Colors.Transparent; plot.DataBackground.Color = Colors.Transparent;
        using D3D12Renderer renderer = new(); renderer.Render(plot, 800, 600);
        using SKBitmap bitmap = renderer.CaptureBitmap();
        Assert.That(bitmap.GetPixel(1, 1).Alpha, Is.Zero);
    }

    /// <summary>Simulates loss of the owned Skia context and verifies explicit failure and deterministic cleanup.</summary>
    [Test]
    public void LostContextFailsWithoutCpuFallbackAndStillDisposes()
    {
        using Plot plot = Scene();
        D3D12Renderer renderer = new();
        renderer.Render(plot, 400, 300);
        renderer.Device.Context.AbandonContext();
        Assert.That(renderer.Device.IsDeviceLost, Is.True);
        Assert.Throws<InvalidOperationException>(() => renderer.Render(plot, 400, 300));
        Assert.That(renderer.PixelReadbackCount, Is.Zero);
        Assert.DoesNotThrow(renderer.Dispose);
        using D3D12Renderer fresh = new();
        fresh.Render(plot, 400, 300);
        Assert.That(fresh.Device.IsDeviceLost, Is.False);
    }
    private static Plot Scene()
    {
        Plot plot = new();
        double[] xs = Enumerable.Range(0, 2000).Select(i => i * .01).ToArray();
        for (int i = 0; i < 3; i++)
        {
            int curve = i; var signal = plot.Add.SignalXY(xs, xs.Select(x => Math.Sin(x * (curve + 1)) + .1 * Math.Cos(x * 37)).ToArray());
            signal.LineWidth = 2; signal.LineStyle.AntiAlias = true; signal.LegendText = "Signal " + i;
        }
        plot.Add.Bars(new Bar[] { new() { Position = 5, Value = .6, Size = 1 }, new() { Position = 12, Value = .9, Size = 1 } }).LegendText = "Bars";
        plot.Add.Text("GPU full Figure / 文字", 8, -1.25).LabelFontSize = 14;
        plot.Title("Native D3D12 full Figure"); plot.XLabel("Time"); plot.YLabel("Value"); plot.ShowLegend();
        plot.Axes.SetLimits(-1, 21, -1.6, 1.6); return plot;
    }
    private static void AssertEquivalent(Plot plot, string name)
    {
        using D3D12Renderer original = new() { OptimizeSignals = false }, optimized = new();
        for (int i = 0; i < 3; i++) { original.Render(plot, 800, 600); optimized.Render(plot, 800, 600); }
        using SKBitmap a = original.CaptureBitmap(), b = optimized.CaptureBitmap();
        int difference = Difference(a.Bytes, b.Bytes);
        if (difference != 0)
        {
            string folder = Path.Combine(TestContext.CurrentContext.WorkDirectory, "GpuDifferences"); Directory.CreateDirectory(folder);
            File.WriteAllBytes(Path.Combine(folder, name + "-original.png"), a.Encode(SKEncodedImageFormat.Png, 100).ToArray());
            File.WriteAllBytes(Path.Combine(folder, name + "-optimized.png"), b.Encode(SKEncodedImageFormat.Png, 100).ToArray());
        }
        Assert.That(difference, Is.Zero, "Original and optimized GPU Figure differ: " + name);
    }
    private static int Difference(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return int.MaxValue;
        int count = 0; for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) count++; return count;
    }
    /// <summary>Draws a visible mark regardless of requested size to detect accidental custom-renderer elimination.</summary>
    private sealed class CountingMarker : IMarker
    {
        internal int Calls;
        /// <inheritdoc />
        public void Render(SKCanvas canvas, Paint paint, Pixel center, float size, MarkerStyle markerStyle)
        {
            Calls++; paint.IsStroke = false; paint.Color = Colors.Red; canvas.DrawCircle(center.X, center.Y, 1, paint.SKPaint);
        }
    }
}

