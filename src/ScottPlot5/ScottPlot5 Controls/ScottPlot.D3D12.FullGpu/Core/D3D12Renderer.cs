using System.Diagnostics;
using System.Runtime.CompilerServices;
using ScottPlot.D3D12.Rendering.Internal;
using ScottPlot.Rendering.RenderActions;
using SkiaSharp;
using Vortice.Direct3D12;

namespace ScottPlot.D3D12.Rendering;

/// <summary>Records CPU wall times for a full Figure, including native MSAA resolve and verified queue completion.</summary>
/// <param name="Width">The physical viewport width.</param>
/// <param name="Height">The physical viewport height.</param>
/// <param name="RenderMilliseconds">The CPU wall time spent rendering the complete Figure.</param>
/// <param name="SubmitMilliseconds">The wall time spent submitting Skia work and native GPU resolve.</param>
/// <param name="FenceMilliseconds">The actual queue-completion wait time.</param>
/// <param name="TotalMilliseconds">The complete render-through-fence wall time, excluding the platform compositor.</param>
/// <param name="AllocatedBytes">The managed allocation count on the rendering thread.</param>
/// <param name="TargetAllocations">The number of target capacity allocations during this renderer's lifetime.</param>
public readonly record struct D3D12FrameStatistics(int Width, int Height, double RenderMilliseconds, double SubmitMilliseconds,
    double FenceMilliseconds, double TotalMilliseconds, long AllocatedBytes, int TargetAllocations);

/// <summary>Renders ordinary complete ScottPlot Figures onto native D3D12 targets with explicit GPU resolve.</summary>
/// <remarks>The renderer is thread-affine. No Raster surface, per-frame pixel readback, or application-level CPU rendering fallback exists.</remarks>
public sealed partial class D3D12Renderer : IDisposable
{
    private readonly D3D12DeviceLease? Lease;
    private readonly GpuRenderTarget Target;
    private readonly ConditionalWeakTable<Plot, PlotState> Optimizers = new();
    private bool Disposed;
    /// <summary>Gets the borrowed graphics device used by this renderer.</summary>
    public D3D12GraphicsDevice Device { get; }
    /// <summary>Gets the capacity width of the retained GPU textures.</summary>
    public int CapacityWidth => Target.Width;
    /// <summary>Gets the capacity height of the retained GPU textures.</summary>
    public int CapacityHeight => Target.Height;
    /// <summary>Gets the borrowed single-sample output texture after the last completed render.</summary>
    public ID3D12Resource ResolvedTexture => Target.Resource ?? throw new InvalidOperationException("Render a Figure before requesting its output texture.");
    /// <summary>Gets statistics from the most recently completed full Figure.</summary>
    public D3D12FrameStatistics LastFrame { get; private set; }
    /// <summary>Gets or sets whether the equivalent built-in SignalXY sampling and path optimizations are used.</summary>
    /// <remarks>Disabling this keeps ordinary Plot.Render on the same D3D12 GPU canvas; it does not enable a CPU drawing fallback.</remarks>
    public bool OptimizeSignals { get; set; } = true;

    /// <summary>Initializes a renderer using the shared hardware device on the current thread.</summary>
    public D3D12Renderer()
    {
        Lease = D3D12GraphicsDevice.AcquireShared(); Device = Lease.Device;
        Target = new(Device.NativeDevice, Device.Context, Device.CommandQueue);
    }
    /// <summary>Initializes a renderer borrowing a caller-owned hardware device.</summary>
    /// <param name="device">The graphics device owned by the caller.</param>
    public D3D12Renderer(D3D12GraphicsDevice device)
    {
        Device = device ?? throw new ArgumentNullException(nameof(device)); Device.VerifyAccess();
        Target = new(Device.NativeDevice, Device.Context, Device.CommandQueue);
    }

    /// <summary>Renders a complete ordinary Plot at the given physical viewport dimensions.</summary>
    /// <param name="plot">The unmodified caller-owned Plot.</param>
    /// <param name="width">The physical viewport width.</param>
    /// <param name="height">The physical viewport height.</param>
    public void Render(Plot plot, int width, int height) => RenderCore(plot ?? throw new ArgumentNullException(nameof(plot)), null, width, height);
    /// <summary>Renders a complete multiplot while preserving its layouts, shared axes and render actions.</summary>
    /// <param name="multiplot">The caller-owned multiplot.</param>
    /// <param name="width">The physical viewport width.</param>
    /// <param name="height">The physical viewport height.</param>
    public void Render(IMultiplot multiplot, int width, int height) => RenderCore(null, multiplot ?? throw new ArgumentNullException(nameof(multiplot)), width, height);

    private void RenderCore(Plot? plot, IMultiplot? multiplot, int width, int height)
    {
        ObjectDisposedException.ThrowIf(Disposed, this); Device.VerifyAccess();
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "A positive physical viewport is required.");
        if (!Target.Fits(width, height)) { Device.WaitForIdle(); Target.Ensure(width, height); }
        long bytes = GC.GetAllocatedBytesForCurrentThread(), start = Stopwatch.GetTimestamp();
        SKCanvas canvas = Target.Surface!.Canvas;
        int save = canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect(0, 0, width, height));
            if (plot is not null) RenderPlot(plot, canvas, null);
            else RenderMultiplot(multiplot!, canvas, new PixelRect(0, width, height, 0));
        }
        finally { canvas.RestoreToCount(save); }
        long rendered = Stopwatch.GetTimestamp();
        Target.SubmitForExternalUse();
        long submitted = Stopwatch.GetTimestamp();
        Device.WaitForIdle();
        long completed = Stopwatch.GetTimestamp();
        LastFrame = new(width, height, Milliseconds(start, rendered), Milliseconds(rendered, submitted),
            Milliseconds(submitted, completed), Milliseconds(start, completed),
            GC.GetAllocatedBytesForCurrentThread() - bytes, Target.Allocations);
    }

    private void RenderMultiplot(IMultiplot multiplot, SKCanvas canvas, PixelRect rect)
    {
        if (multiplot.GetType() != typeof(Multiplot)) { multiplot.Render(canvas, rect); return; }
        multiplot.SharedAxes.UpdateSharedPlotAxisLimits();
        foreach (IMultiplotPreRenderAction action in multiplot.PreRenderActions) action.Invoke();
        canvas.Clear();
        PixelRect[] rectangles = multiplot.Layout.GetSubplotRectangles(multiplot.Subplots, rect);
        for (int i = 0; i < multiplot.Subplots.Count; i++)
        {
            Plot plot = multiplot.Subplots.GetPlot(i);
            bool clear = plot.RenderManager.ClearCanvasBeforeEachRender;
            plot.RenderManager.ClearCanvasBeforeEachRender = false;
            try { RenderPlot(plot, canvas, rectangles[i]); }
            finally { plot.RenderManager.ClearCanvasBeforeEachRender = clear; }
            multiplot.LastRender.Remember(plot, rectangles[i]);
        }
    }

    private void RenderPlot(Plot plot, SKCanvas canvas, PixelRect? rectangle)
    {
        lock (plot.Sync)
        {
            List<IRenderAction> actions = plot.RenderManager.RenderActions;
            PlotState state = Optimizers.GetValue(plot, static _ => new());
            List<(int Index, IRenderAction Original)> replaced = state.Replaced;
            replaced.Clear();
            int saved = canvas.Save();
            try
            {
                if (OptimizeSignals)
                {
                    OptimizedPlottables optimized = state.Optimizer;
                    for (int i = 0; i < actions.Count; i++)
                        if (actions[i].GetType() == typeof(RenderPlottables)) { replaced.Add((i, actions[i])); actions[i] = optimized; }
                }
                if (rectangle is PixelRect rect) plot.Render(canvas, rect); else plot.Render(Target.Surface!);
            }
            finally
            {
                foreach (var item in replaced) actions[item.Index] = item.Original;
                replaced.Clear();
                canvas.RestoreToCount(saved);
            }
        }
    }
    /// <summary>Retains action bookkeeping across frames without replacing public Plot objects.</summary>
    private sealed class PlotState
    {
        internal readonly OptimizedPlottables Optimizer = new();
        internal readonly List<(int Index, IRenderAction Original)> Replaced = [];
    }
    private static double Milliseconds(long begin, long end) => (end - begin) * 1000.0 / Stopwatch.Frequency;

    /// <inheritdoc />
    public void Dispose()
    {
        if (Disposed) return;
        Device.VerifyOwnerThread();
        try { if (!Device.IsDeviceLost) { Device.Context.Flush(true, false); Device.WaitForIdle(); } }
        catch (Exception) when (Device.IsDeviceLost) { }
        finally
        {
            foreach (var optimizer in Optimizers) optimizer.Value.Optimizer.Dispose();
            Optimizers.Clear(); Target.Dispose(); Disposed = true; Lease?.Dispose();
        }
        GC.SuppressFinalize(this);
    }
}



