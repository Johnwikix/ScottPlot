using FluentAssertions;
using NUnit.Framework;
using ScottPlot;
using ScottPlot.Plottables;
using SkiaSharp;
using System.Reflection;

namespace ScottPlot.D3D12.Tests;

public class RenderingTests
{
    [TestCase(true)]
    [TestCase(false)]
    public void GpuLineIsClippedToDataRect(bool antiAlias)
    {
        using Plot plot = CreatePlot(20);
        D3D12SignalXY signal = plot.Add.D3D12SignalXY([-50.0, 150.0], [-50.0, 150.0], Colors.Red);
        signal.LineWidth = 5;
        signal.LineStyle.AntiAlias = antiAlias;
        using IDisposable registration = plot.UseD3D12();

        using SKBitmap bitmap = Render(plot, 200, 200);

        bitmap.GetPixel(100, 100).Should().Be(SKColors.Red);
        bitmap.GetPixel(5, 5).Should().Be(SKColors.Black);
        bitmap.GetPixel(195, 195).Should().Be(SKColors.Black);
        GetGpuBatchCount(plot).Should().BeGreaterThan(0);
    }

    [Test]
    public void SoftwarePlottableSplitsGpuBatchesAndPreservesOrder()
    {
        using Plot plot = CreatePlot();
        plot.Add.D3D12SignalXY([0.0, 100.0], [50.0, 50.0], Colors.Red).LineWidth = 6;
        plot.Add.Plottable(new SolidRectPlottable(SKColors.Green));
        plot.Add.D3D12SignalXY([0.0, 100.0], [50.0, 50.0], Colors.Blue).LineWidth = 6;
        using IDisposable registration = plot.UseD3D12();

        using SKBitmap bitmap = Render(plot, 200, 200);

        bitmap.GetPixel(100, 100).Should().Be(SKColors.Blue);
        GetGpuBatchCount(plot).Should().BeGreaterThanOrEqualTo(2);
    }

    [TestCase(1f)]
    [TestCase(1.5f)]
    [TestCase(2f)]
    public void ScaleFactorUsesPhysicalRenderTarget(float scale)
    {
        using Plot plot = CreatePlot();
        plot.ScaleFactor = scale;
        plot.Add.D3D12SignalXY([0.0, 100.0], [0.0, 100.0], Colors.Red).LineWidth = 4;
        using IDisposable registration = plot.UseD3D12();
        int size = (int)(100 * scale);

        using SKBitmap bitmap = Render(plot, size, size);

        bitmap.GetPixel(size / 2, size / 2).Should().Be(SKColors.Red);
        GetGpuBatchCount(plot).Should().BeGreaterThan(0);
    }

    [Test]
    public void DataMutationIsRenderedWithoutCountChange()
    {
        double[] xs = [0, 100];
        double[] ys = [25, 25];
        using Plot plot = CreatePlot();
        plot.Add.D3D12SignalXY(xs, ys, Colors.Red).LineWidth = 5;
        using IDisposable registration = plot.UseD3D12();

        using (SKBitmap initial = Render(plot, 200, 200))
            initial.GetPixel(100, 150).Should().Be(SKColors.Red);

        ys[0] = ys[1] = 75;
        using SKBitmap changed = Render(plot, 200, 200);
        changed.GetPixel(100, 150).Should().Be(SKColors.Black);
        changed.GetPixel(100, 50).Should().Be(SKColors.Red);
    }

    [Test]
    public void DenseSignalHasNoEmptyInteriorColumns()
    {
        double[] xs = new double[10_000];
        double[] ys = new double[xs.Length];
        for (int i = 0; i < xs.Length; i++)
        {
            xs[i] = i;
            ys[i] = i % 2 * 100;
        }

        using Plot plot = CreatePlot();
        plot.Axes.SetLimits(0, xs.Length - 1, 0, 100);
        D3D12SignalXY signal = plot.Add.D3D12SignalXY(xs, ys, Colors.Red);
        signal.LineWidth = 2;
        signal.LineStyle.AntiAlias = false;
        using IDisposable registration = plot.UseD3D12();

        using SKBitmap bitmap = Render(plot, 200, 200);

        for (int x = 1; x < bitmap.Width - 1; x++)
            bitmap.GetPixel(x, bitmap.Height / 2).Should().Be(SKColors.Red, "column {0} must remain continuous", x);
        GetGpuBatchCount(plot).Should().BeGreaterThan(0);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AxisLimitChangeRendersZoomedView(bool antiAlias)
    {
        using Plot plot = CreatePlot();
        D3D12SignalXY signal = plot.Add.D3D12SignalXY([0.0, 100.0], [25.0, 25.0], Colors.Red);
        signal.LineWidth = 5;
        signal.LineStyle.AntiAlias = antiAlias;
        using IDisposable registration = plot.UseD3D12();

        using (SKBitmap initial = Render(plot, 200, 200))
            initial.GetPixel(100, 150).Should().Be(SKColors.Red);

        plot.Axes.SetLimits(25, 75, 20, 30);
        using SKBitmap zoomed = Render(plot, 200, 200);
        zoomed.GetPixel(100, 100).Should().Be(SKColors.Red);
        zoomed.GetPixel(100, 150).Should().Be(SKColors.Black);
        GetGpuBatchCount(plot).Should().BeGreaterThanOrEqualTo(2);
    }

    [TestCase(true)]
    [TestCase(false)]
    public void AcuteMiterJoinMatchesSoftwareBounds(bool antiAlias)
    {
        double[] xs = [10, 90, 10];
        double[] ys = [30, 70, 70];
        using Plot cpuPlot = CreatePlot();
        using Plot gpuPlot = CreatePlot();
        D3D12SignalXY cpuSignal = cpuPlot.Add.D3D12SignalXY(xs, ys, Colors.Red);
        D3D12SignalXY gpuSignal = gpuPlot.Add.D3D12SignalXY(xs, ys, Colors.Red);
        foreach (D3D12SignalXY signal in new[] { cpuSignal, gpuSignal })
        {
            signal.LineWidth = 12;
            signal.LineStyle.AntiAlias = antiAlias;
            signal.LineStyle.StrokeMiter = 4;
        }

        using IDisposable registration = gpuPlot.UseD3D12();
        using SKBitmap expected = Render(cpuPlot, 200, 200, requireGpu: false);
        using SKBitmap actual = Render(gpuPlot, 200, 200);
        (int Left, int Top, int Right, int Bottom) expectedBounds = GetInkBounds(expected);
        (int Left, int Top, int Right, int Bottom) actualBounds = GetInkBounds(actual);

        actualBounds.Left.Should().BeInRange(expectedBounds.Left - 2, expectedBounds.Left + 2);
        actualBounds.Top.Should().BeInRange(expectedBounds.Top - 2, expectedBounds.Top + 2);
        actualBounds.Right.Should().BeInRange(expectedBounds.Right - 2, expectedBounds.Right + 2);
        actualBounds.Bottom.Should().BeInRange(expectedBounds.Bottom - 2, expectedBounds.Bottom + 2);
    }

    [Test]
    public void UnsupportedStyleUsesOriginalSoftwareRenderer()
    {
        using Plot plot = CreatePlot();
        D3D12SignalXY signal = plot.Add.D3D12SignalXY([0.0, 100.0], [50.0, 50.0], Colors.Red);
        signal.LinePattern = LinePattern.Dashed;
        signal.LineWidth = 4;
        using IDisposable registration = plot.UseD3D12();

        using SKBitmap bitmap = Render(plot, 200, 200, requireGpu: false);

        GetGpuBatchCount(plot).Should().Be(0);
        bitmap.GetPixel(100, 100).Should().NotBe(SKColors.Black);
    }

    private static Plot CreatePlot(int padding = 0)
    {
        Plot plot = new()
        {
            ScaleFactor = 1,
        };
        plot.Axes.Frameless();
        plot.HideGrid();
        plot.Layout.Fixed(new PixelPadding(padding));
        plot.FigureBackground.Color = Colors.Black;
        plot.DataBackground.Color = Colors.Black;
        plot.Axes.SetLimits(0, 100, 0, 100);
        return plot;
    }

    private static SKBitmap Render(Plot plot, int width, int height, bool requireGpu = true)
    {
        long gpuBefore = requireGpu ? GetGpuBatchCount(plot) : 0;
        long fallbackBefore = requireGpu ? GetFallbackBatchCount(plot) : 0;
        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        SKSurface surface = SKSurface.Create(info) ?? throw new InvalidOperationException("Unable to create test surface.");
        try
        {
            plot.Render(surface.Canvas, new PixelRect(0, width, height, 0));
            surface.Canvas.Flush();
            if (requireGpu)
            {
                GetGpuBatchCount(plot).Should().BeGreaterThan(gpuBefore, "this render must use the GPU path");
                GetFallbackBatchCount(plot).Should().Be(fallbackBefore, "this render must not fall back to software");
            }

            using SKImage image = surface.Snapshot();
            return SKBitmap.FromImage(image);
        }
        finally
        {
            surface.Dispose();
        }
    }

    private static (int Left, int Top, int Right, int Bottom) GetInkBounds(SKBitmap bitmap)
    {
        int left = bitmap.Width;
        int top = bitmap.Height;
        int right = -1;
        int bottom = -1;
        for (int y = 0; y < bitmap.Height; y++)
        {
            for (int x = 0; x < bitmap.Width; x++)
            {
                if (bitmap.GetPixel(x, y) == SKColors.Black)
                    continue;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < 0)
            throw new InvalidOperationException("The rendered image contains no curve pixels.");
        return (left, top, right, bottom);
    }

    private static long GetGpuBatchCount(Plot plot) => GetBatchCount(plot, "GpuBatchCount");

    private static long GetFallbackBatchCount(Plot plot) => GetBatchCount(plot, "FallbackBatchCount");

    private static long GetBatchCount(Plot plot, string propertyName)
    {
        object action = plot.RenderManager.RenderActions.Single(x => x.GetType().Name == "D3D12RenderPlottables");
        PropertyInfo property = action.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Batch counter not found: {propertyName}");
        return (long)(property.GetValue(action) ?? 0L);
    }

    private sealed class SolidRectPlottable(SKColor color) : IPlottable
    {
        public bool IsVisible { get; set; } = true;
        public IAxes Axes { get; set; } = new Axes();
        public IEnumerable<LegendItem> LegendItems => [];
        public AxisLimits GetAxisLimits() => AxisLimits.NoLimits;

        public void Render(RenderPack rp)
        {
            using SKPaint paint = new()
            {
                Color = color,
                Style = SKPaintStyle.Fill,
            };
            rp.Canvas.DrawRect(rp.DataRect.ToSKRect(), paint);
        }
    }
}
