using ScottPlot.Plottables;
using SkiaSharp;

namespace ScottPlot.D3D12.Rendering;

internal sealed class D3D12RenderPlottables : IRenderAction, IDisposable
{
    private readonly D3D12CurveRenderer Renderer = new();
    private readonly List<D3D12SignalXY> Batch = [];

    internal long GpuBatchCount { get; private set; }
    internal long FallbackBatchCount { get; private set; }

    public void Render(RenderPack rp)
    {
        IPlottable[] visiblePlottables = rp.Plot.PlottableList.Where(x => x.IsVisible).ToArray();
        Batch.Clear();

        foreach (IPlottable plottable in visiblePlottables)
        {
            plottable.Axes.DataRect = rp.DataRect;
            if (plottable is D3D12SignalXY signal && D3D12PlottableSupport.CanRenderGpu(signal))
            {
                if (Batch.Count > 0 && Batch[0].LineStyle.AntiAlias != signal.LineStyle.AntiAlias)
                    Flush(rp);
                Batch.Add(signal);
                continue;
            }

            Flush(rp);
            D3D12PlottableSupport.RenderSoftware(rp, plottable);
        }

        Flush(rp);
    }

    private void Flush(RenderPack rp)
    {
        if (Batch.Count == 0)
            return;

        bool rendered;
        rp.CanvasState.Save();
        try
        {
            rp.CanvasState.Clip(rp.DataRect);
            rendered = Renderer.Render(rp, Batch);
        }
        finally
        {
            rp.CanvasState.DisableClipping();
        }

        if (rendered)
        {
            GpuBatchCount++;
        }
        else
        {
            FallbackBatchCount++;
            foreach (D3D12SignalXY signal in Batch)
                D3D12PlottableSupport.RenderSoftware(rp, signal);
        }

        Batch.Clear();
    }

    public void Dispose() => Renderer.Dispose();
}
