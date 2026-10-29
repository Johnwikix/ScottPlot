using ScottPlot.Plottables;
using ScottPlot.Rendering.RenderActions;
using SkiaSharp;

namespace ScottPlot.D3D12.Rendering;

internal sealed class D3D12CompositionAction(
    D3D12NativePresenter presenter,
    IRenderAction plottableAction,
    IRenderAction[] suffixActions,
    Action<SKPicture?> overlayChanged) : IRenderAction
{
    public void Render(RenderPack rp)
    {
        D3D12PlottablePlan plan = D3D12PlottablePlan.Create(rp.Plot, requireSingleAntiAliasMode: true);
        if (!plan.HasGpuRun || plan.RequiresFullSoftware)
        {
            RenderFullSoftware(rp);
            return;
        }

        SKPicture? overlay = RecordOverlay(rp, plan);
        if (overlay is null)
        {
            RenderFullSoftware(rp);
            return;
        }

        overlayChanged(overlay);
        foreach (IPlottable plottable in plan.VisiblePlottables)
            plottable.Axes.DataRect = rp.DataRect;

        for (int i = 0; i < plan.FirstGpuIndex; i++)
            D3D12PlottableSupport.RenderSoftware(rp, plan.VisiblePlottables[i]);

        List<D3D12SignalXY> signals = plan.VisiblePlottables
            .Skip(plan.FirstGpuIndex)
            .Take(plan.LastGpuIndex - plan.FirstGpuIndex + 1)
            .Cast<D3D12SignalXY>()
            .ToList();
        if (!presenter.Render(rp, signals))
        {
            presenter.Clear();
            foreach (D3D12SignalXY signal in signals)
                D3D12PlottableSupport.RenderSoftware(rp, signal);
        }
    }

    private void RenderFullSoftware(RenderPack rp)
    {
        presenter.Clear();
        overlayChanged(null);
        Execute(rp, plottableAction);
        foreach (IRenderAction action in suffixActions)
            Execute(rp, action);
    }

    private SKPicture? RecordOverlay(RenderPack bottomRp, D3D12PlottablePlan plan)
    {
        using SKPictureRecorder recorder = new();
        SKCanvas canvas = recorder.BeginRecording(bottomRp.FigureRect.ToSKRect());
        canvas.Scale((float)bottomRp.Plot.ScaleFactor);
        using RenderPack topRp = new(bottomRp.Plot, bottomRp.FigureRect, canvas);
        new CalculateLayout().Render(topRp);
        if (!topRp.DataRect.Equals(bottomRp.DataRect))
        {
            recorder.EndRecording().Dispose();
            return null;
        }

        foreach (IPlottable plottable in plan.VisiblePlottables)
            plottable.Axes.DataRect = topRp.DataRect;
        for (int i = plan.LastGpuIndex + 1; i < plan.VisiblePlottables.Length; i++)
            D3D12PlottableSupport.RenderSoftware(topRp, plan.VisiblePlottables[i]);

        foreach (IRenderAction action in suffixActions)
        {
            if (action is RenderBenchmark)
                RenderBenchmark(topRp, bottomRp.Elapsed);
            else
                Execute(topRp, action);
        }

        return recorder.EndRecording();
    }

    private static void RenderBenchmark(RenderPack rp, TimeSpan elapsed)
    {
        if (rp.Plot.Benchmark is not Benchmark benchmark || !benchmark.IsVisible)
            return;

        benchmark.LabelStyle.Text = $"Rendered in {elapsed.TotalMilliseconds:0.000} ms ({1e3 / elapsed.TotalMilliseconds:N0} FPS)";
        benchmark.LabelStyle.Render(rp.Canvas, rp.DataRect.BottomLeft.WithOffset(10, -13), rp.Paint);
    }

    private static void Execute(RenderPack rp, IRenderAction action)
    {
        rp.CanvasState.Save();
        action.Render(rp);
        rp.CanvasState.RestoreAll();
    }
}
