using FluentAssertions;
using NUnit.Framework;
using ScottPlot;
using SkiaSharp;
using System.Diagnostics;
using System.Reflection;

namespace ScottPlot.D3D12.Tests;

public class PerformanceTests
{
    [Test, Explicit("Runs a local CPU/GPU rendering comparison.")]
    public void MeasureCpuAndGpuReadback()
    {
        const int curveCount = 32;
        const int pointCount = 20_000;
        const int width = 800;
        const int height = 600;
        const int iterations = 12;
        double[] xs = new double[pointCount];
        double[][] ys = new double[curveCount][];
        for (int i = 0; i < pointCount; i++)
            xs[i] = i * 0.01;

        for (int curve = 0; curve < curveCount; curve++)
        {
            ys[curve] = new double[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                ys[curve][i] = Math.Sin(xs[i] * (1 + curve % 5) + curve * 0.1) +
                    Math.Sin(xs[i] * 50 + curve) * 0.2;
            }
        }

        using Plot cpuPlot = CreatePlot(xs, ys);
        using Plot gpuPlot = CreatePlot(xs, ys);
        using IDisposable registration = gpuPlot.UseD3D12();
        using SKSurface cpuSurface = CreateSurface(width, height);
        using SKSurface gpuSurface = CreateSurface(width, height);

        for (int i = 0; i < 3; i++)
        {
            cpuPlot.Render(cpuSurface.Canvas, width, height);
            gpuPlot.Render(gpuSurface.Canvas, width, height);
        }

        long gpuBeforeMeasurement = GetGpuBatchCount(gpuPlot);
        long fallbackBeforeMeasurement = GetFallbackBatchCount(gpuPlot);
        List<double> cpuTimes = [];
        List<double> gpuTimes = [];
        for (int i = 0; i < iterations; i++)
        {
            if (i % 2 == 0)
            {
                cpuTimes.Add(Measure(() => cpuPlot.Render(cpuSurface.Canvas, width, height)));
                gpuTimes.Add(Measure(() => gpuPlot.Render(gpuSurface.Canvas, width, height)));
            }
            else
            {
                gpuTimes.Add(Measure(() => gpuPlot.Render(gpuSurface.Canvas, width, height)));
                cpuTimes.Add(Measure(() => cpuPlot.Render(cpuSurface.Canvas, width, height)));
            }
        }

        double cpuMedian = Median(cpuTimes);
        double gpuMedian = Median(gpuTimes);
        long measuredGpuBatches = GetGpuBatchCount(gpuPlot) - gpuBeforeMeasurement;
        long measuredFallbackBatches = GetFallbackBatchCount(gpuPlot) - fallbackBeforeMeasurement;
        measuredGpuBatches.Should().BeGreaterThanOrEqualTo(iterations);
        measuredFallbackBatches.Should().Be(0, "every measured iteration must remain on the GPU path");
        TestContext.Out.WriteLine($"CPU median: {cpuMedian:F3} ms; D3D12 readback median: {gpuMedian:F3} ms; speedup: {cpuMedian / gpuMedian:F2}x; measured GPU batches: {measuredGpuBatches}");
    }

    private static Plot CreatePlot(double[] xs, double[][] ys)
    {
        Plot plot = new();
        plot.Axes.Frameless();
        plot.HideGrid();
        plot.Layout.Fixed(new PixelPadding(0));
        plot.FigureBackground.Color = Colors.Black;
        plot.DataBackground.Color = Colors.Black;
        plot.Axes.SetLimits(xs[0], xs[^1], -1.5, 1.5);
        foreach (double[] values in ys)
            plot.Add.D3D12SignalXY(xs, values).LineWidth = 2;
        return plot;
    }

    private static SKSurface CreateSurface(int width, int height)
    {
        SKImageInfo info = new(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
        return SKSurface.Create(info) ?? throw new InvalidOperationException("Unable to create benchmark surface.");
    }

    private static double Measure(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        return (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
    }

    private static double Median(List<double> values)
    {
        double[] sorted = values.Order().ToArray();
        return sorted[sorted.Length / 2];
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
}
