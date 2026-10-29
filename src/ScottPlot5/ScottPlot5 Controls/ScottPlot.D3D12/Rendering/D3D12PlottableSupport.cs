using ScottPlot.Plottables;
using SkiaSharp;

namespace ScottPlot.D3D12.Rendering;

internal static class D3D12PlottableSupport
{
    public static bool CanRenderGpu(D3D12SignalXY signal)
    {
        bool hasMarkers = signal.MarkerStyle.CustomRenderer is not null ||
            (signal.MarkerStyle.IsVisible && signal.MarkerStyle.Size > 0 && signal.MarkerStyle.Shape != MarkerShape.None);

        return signal.Data.Count >= 2 &&
            signal.ConnectStyle == ConnectStyle.Straight &&
            signal.LineStyle.CanBeRendered &&
            signal.LineColor.Alpha == 255 &&
            (signal.LinePattern.Intervals?.Length ?? 0) == 0 &&
            !signal.LineStyle.HandDrawn &&
            signal.LineStyle.StrokeCap == SKStrokeCap.Butt &&
            signal.LineStyle.StrokeJoin == SKStrokeJoin.Miter &&
            !hasMarkers;
    }

    public static void RenderSoftware(RenderPack rp, IPlottable plottable)
    {
        rp.CanvasState.Save();
        if (plottable is IPlottableGL plottableGL)
        {
            plottableGL.Render(rp);
        }
        else
        {
            rp.CanvasState.Clip(rp.DataRect);
            plottable.Render(rp);
        }

        rp.CanvasState.DisableClipping();
    }
}

internal readonly struct D3D12PlottablePlan
{
    public IPlottable[] VisiblePlottables { get; }
    public int FirstGpuIndex { get; }
    public int LastGpuIndex { get; }
    public bool HasGpuRun => FirstGpuIndex >= 0;
    public bool RequiresFullSoftware { get; }

    private D3D12PlottablePlan(IPlottable[] visiblePlottables, int firstGpuIndex, int lastGpuIndex, bool requiresFullSoftware)
    {
        VisiblePlottables = visiblePlottables;
        FirstGpuIndex = firstGpuIndex;
        LastGpuIndex = lastGpuIndex;
        RequiresFullSoftware = requiresFullSoftware;
    }

    public static D3D12PlottablePlan Create(Plot plot, bool requireSingleAntiAliasMode)
    {
        IPlottable[] visible = plot.PlottableList.Where(x => x.IsVisible).ToArray();
        int first = -1;
        int last = -1;
        bool antiAlias = false;
        bool mixedAntiAlias = false;

        for (int i = 0; i < visible.Length; i++)
        {
            if (visible[i] is not D3D12SignalXY signal || !D3D12PlottableSupport.CanRenderGpu(signal))
                continue;

            if (first < 0)
            {
                first = i;
                antiAlias = signal.LineStyle.AntiAlias;
            }
            else if (signal.LineStyle.AntiAlias != antiAlias)
            {
                mixedAntiAlias = true;
            }

            last = i;
        }

        bool interleaved = first >= 0 && visible
            .Skip(first + 1)
            .Take(last - first - 1)
            .Any(x => x is not D3D12SignalXY signal || !D3D12PlottableSupport.CanRenderGpu(signal));

        return new D3D12PlottablePlan(visible, first, last, interleaved || (requireSingleAntiAliasMode && mixedAntiAlias));
    }
}
