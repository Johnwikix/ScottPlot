using System.Diagnostics;
using ScottPlot;

namespace AvaloniaD3D12Probe;

/// <summary>Builds the same complete Figure for each backend and retains arrays for in-place updates.</summary>
internal sealed class FigureFixture
{
    private readonly double[][] Original = new double[32][];
    private readonly double[][] Values = new double[32][];
    private long Start;
    internal long DrawTicks;
    internal readonly Plot Plot;

    internal FigureFixture(Plot plot)
    {
        Plot = plot;
        double[] xs = Enumerable.Range(0, 20_000).Select(i => i * .01).ToArray();
        for (int curve = 0; curve < Values.Length; curve++)
        {
            int index = curve;
            Original[curve] = xs.Select(x => Math.Sin(x * (1 + index % 5) + index * .1) + Math.Sin(x * 50 + index) * .2).ToArray();
            Values[curve] = (double[])Original[curve].Clone();
            var signal = plot.Add.SignalXY(xs, Values[curve]);
            signal.LineWidth = 2;
            signal.LineStyle.AntiAlias = true;
            if (curve < 2) signal.LegendText = $"SignalXY {curve + 1} / 20,000";
        }
        var bars = plot.Add.Bars(new Bar[]
        {
            new() { Position = 30, Value = .6, Size = 6 },
            new() { Position = 90, Value = .8, Size = 6 },
            new() { Position = 150, Value = .4, Size = 6 },
        });
        bars.LegendText = "Bars";
        plot.Add.Text("Full Figure / Skia GPU", 110, -1.3).LabelFontSize = 16;
        plot.Title("Avalonia full Figure / 32 x 20,000 / AA");
        plot.XLabel("Time (s)"); plot.YLabel("Amplitude"); plot.ShowLegend();
        ResetLimits();
        plot.RenderManager.PreRenderLock += (_, _) => Start = Stopwatch.GetTimestamp();
        plot.RenderManager.RenderFinished += (_, _) => Interlocked.Add(ref DrawTicks, Stopwatch.GetTimestamp() - Start);
    }

    internal void ResetLimits() => Plot.Axes.SetLimits(-10, 210, -1.6, 1.6);

    internal void Update(int frame)
    {
        double offset = Math.Sin(frame * .031) * .05;
        for (int curve = 0; curve < Values.Length; curve++)
            for (int i = 0; i < Values[curve].Length; i++) Values[curve][i] = Original[curve][i] + offset;
    }
}
