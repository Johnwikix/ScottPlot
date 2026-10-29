using ScottPlot.DataSources;
using ScottPlot.Plottables;

namespace ScottPlot;

public static class D3D12AddPlottableExtensions
{
    public static D3D12SignalXY D3D12SignalXY(this PlottableAdder add, ISignalXYSource source, Color? color = null)
    {
        D3D12SignalXY signal = new(source)
        {
            Color = color ?? add.GetNextColor(),
        };
        add.Plot.PlottableList.Add(signal);
        return signal;
    }

    public static D3D12SignalXY D3D12SignalXY(this PlottableAdder add, double[] xs, double[] ys, Color? color = null)
    {
        SignalXYSourceDoubleArray source = new(xs, ys);
        return add.D3D12SignalXY(source, color);
    }
}
