using ScottPlot.D3D12.Rendering;
using ScottPlot.Rendering.RenderActions;

namespace ScottPlot;

public static class D3D12PlotExtensions
{
    public static IDisposable UseD3D12(this Plot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);

        lock (plot.Sync)
        {
            List<IRenderAction> actions = plot.RenderManager.RenderActions;
            if (actions.Any(x => x is D3D12RenderPlottables))
                throw new InvalidOperationException("D3D12 rendering is already enabled for this plot.");

            int index = actions.FindIndex(x => x is RenderPlottables);
            if (index < 0)
                throw new InvalidOperationException("The plot does not contain the standard plottable render action.");

            IRenderAction original = actions[index];
            D3D12RenderPlottables replacement = new();
            actions[index] = replacement;
            return new D3D12Registration(plot, original, replacement);
        }
    }

    private sealed class D3D12Registration(Plot plot, IRenderAction original, D3D12RenderPlottables replacement) : IDisposable
    {
        private Plot? Plot = plot;

        public void Dispose()
        {
            Plot? plot = Interlocked.Exchange(ref Plot, null);
            if (plot is null)
                return;

            lock (plot.Sync)
            {
                int index = plot.RenderManager.RenderActions.IndexOf(replacement);
                if (index >= 0)
                    plot.RenderManager.RenderActions[index] = original;
            }

            replacement.Dispose();
        }
    }
}
