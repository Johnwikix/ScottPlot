using ScottPlot;
using ScottPlot.Plottables;

/// <summary>Describes isolated sampling/path interventions for the same full Figure.</summary>
internal sealed record Variant(string Name, bool Fast, bool Shared, bool Simd, bool Bulk, bool SkipZeroMarker = false);

/// <summary>Retains mutable arrays while rebuilding all sampling and paths for every Figure.</summary>
internal sealed class FigureFixture : IDisposable
{
    internal readonly Plot Plot = new();
    internal readonly FrameClock Clock = new();
    internal readonly List<FastSignalSource> Sources = [];
    internal readonly List<SignalXY> Signals = [];
    internal readonly List<BulkPathLine> Paths = [];
    private readonly double[][] Original = new double[32][];
    private readonly double[][] Values = new double[32][];

    internal FigureFixture(Variant variant)
    {
        double[] xs = Enumerable.Range(0, 20_000).Select(i => i * .01).ToArray();
        ColumnPlan? shared = variant.Shared ? new() : null;
        for (int curve = 0; curve < 32; curve++)
        {
            int index = curve;
            Original[curve] = xs.Select(x => Math.Sin(x * (1 + index % 5) + index * .1) + Math.Sin(x * 50 + index) * .2).ToArray();
            Values[curve] = (double[])Original[curve].Clone();
            SignalXY signal;
            if (variant.Fast)
            {
                FastSignalSource fast = new(xs, Values[curve], Clock, shared, variant.Simd);
                Sources.Add(fast);
                signal = Plot.Add.SignalXY(fast);
            }
            else signal = Plot.Add.SignalXY(xs, Values[curve]);
            Signals.Add(signal);
            signal.LineWidth = 2;
            signal.LineStyle.AntiAlias = true;
            if (curve < 2) signal.LegendText = $"SignalXY {curve + 1} / 20,000";
            if (variant.Bulk)
            {
                BulkPathLine path = new(signal, variant.SkipZeroMarker);
                Paths.Add(path);
                Plot.PlottableList[^1] = path;
            }
        }
        var bars = Plot.Add.Bars(new Bar[] { new() { Position = 30, Value = .6, Size = 6 },
            new() { Position = 90, Value = .8, Size = 6 }, new() { Position = 150, Value = .4, Size = 6 } });
        bars.LegendText = "Bars";
        Plot.Add.Text("Full Figure D3D12", 110, -1.3).LabelFontSize = 16;
        Plot.Title("D3D12 full Figure / 32 x 20,000 / AA");
        Plot.XLabel("Time (s)"); Plot.YLabel("Amplitude"); Plot.ShowLegend();
        Plot.Axes.SetLimits(-10, 210, -1.6, 1.6);
        Plot.RenderManager.PreRenderLock += (_, _) => Clock.Begin();
    }

    internal void UpdateData(int frame)
    {
        double offset = frame < 0 ? 0 : Math.Sin(frame * .031) * .05;
        for (int curve = 0; curve < Values.Length; curve++)
            for (int i = 0; i < Values[curve].Length; i++) Values[curve][i] = Original[curve][i] + offset;
    }

    internal void ClearCounters()
    {
        foreach (var s in Sources) s.DataTicks = s.VectorRanges = s.SpecialScalarRanges = 0;
        foreach (var p in Paths) p.PathTicks = 0;
    }

    /// <inheritdoc />
    public void Dispose() { foreach (var path in Paths) path.Dispose(); Plot.Dispose(); }
}


