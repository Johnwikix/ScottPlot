using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.D3D12.Rendering;
using SkiaSharp;
using PixelSize = Avalonia.PixelSize;

namespace AvaloniaD3D12Probe;

/// <summary>Hosts the existing full-Figure D3D12 renderer in Avalonia's composition tree.</summary>
/// <remarks>Reuses AvaPlot's menu, model and keyboard handling, with physical-pixel pointer coordinates.</remarks>
internal sealed class GpuPlot : AvaPlot, IPlotControl, IAsyncDisposable
{
    private readonly SemaphoreSlim Gate = new(1);
    private CompositionSurfaceVisual? Visual;
    private CompositionDrawingSurface? Surface;
    private GpuPresenter? Presenter;
    private D3D12Renderer? Renderer;
    private bool Queued;
    private bool Disposed;
    internal bool OptimizeSignals { get; set; }
    internal bool AutomaticRefresh { get; set; } = true;
    internal double? TestRenderScale { get; set; }
    internal Exception? Failure { get; private set; }
    internal PixelSize LastSize { get; private set; }
    internal long Frames { get; private set; }
    internal long PointerWheelEvents { get; private set; }
    internal long PointerMoveEvents { get; private set; }
    internal long Readbacks => Renderer?.PixelReadbackCount ?? 0;
    internal int TextureAllocations => Presenter?.TextureAllocations ?? 0;
    internal long Copies => Presenter?.Copies ?? 0;
    internal long CompletedUpdates => Presenter?.CompletedUpdates ?? 0;
    internal double RenderMilliseconds => Renderer?.LastFrame.TotalMilliseconds ?? 0;
    internal string AdapterName => Renderer?.Device.AdapterName ?? "not initialized";
    internal double RenderScale => TestRenderScale ?? TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
    internal event Action? FramePresented;

    /// <inheritdoc />
    public new GRContext? GRContext => Renderer?.Device.Context;

    /// <inheritdoc />
    public override void Render(DrawingContext context) { }

    /// <inheritdoc />
    public new void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Refresh); return; }
        if (Disposed || !AutomaticRefresh || Queued || TopLevel.GetTopLevel(this) is null) return;
        Queued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            Queued = false;
            if (Disposed || !AutomaticRefresh || TopLevel.GetTopLevel(this) is null) return;
            try { await RenderFrameAsync(); }
            catch (Exception error) { Failure = error; }
        }, DispatcherPriority.Background);
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == BoundsProperty) Refresh();
    }

    internal async Task RenderFrameAsync()
    {
        await Gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(Disposed, this);
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            var elementVisual = ElementComposition.GetElementVisual(this);
            if (elementVisual is null) return;
            Compositor compositor = elementVisual.Compositor;
            if (Renderer is null)
            {
                ICompositionGpuInterop interop = await compositor.TryGetCompositionGpuInterop()
                    ?? throw new PlatformNotSupportedException("Avalonia GPU interop unavailable; no fallback.");
                Renderer = new();
                Surface = compositor.CreateDrawingSurface();
                Presenter = new(Renderer, interop, Surface);
                Visual = compositor.CreateSurfaceVisual();
                Visual.Surface = Surface;
                ElementComposition.SetElementChildVisual(this, Visual);
            }
            double scale = RenderScale;
            LastSize = new((int)Math.Ceiling(Bounds.Width * scale), (int)Math.Ceiling(Bounds.Height * scale));
            DisplayScale = (float)scale;
            for (int i = 0; i < Multiplot.Subplots.Count; i++) Multiplot.Subplots.GetPlot(i).ScaleFactor = scale;
            Visual!.Size = new(Bounds.Width, Bounds.Height);
            Renderer.OptimizeSignals = OptimizeSignals;
            long before = Renderer.PixelReadbackCount;
            Renderer.Render(Multiplot, LastSize.Width, LastSize.Height);
            await Presenter!.PresentAsync(LastSize);
            if (Renderer.PixelReadbackCount != before) throw new InvalidOperationException("Unexpected pixel readback in presentation loop.");
            Frames++;
            FramePresented?.Invoke();
        }
        finally { Gate.Release(); }
    }

    internal Pixel PhysicalPixel(Point point) => new((float)(point.X * RenderScale), (float)(point.Y * RenderScale));

    /// <inheritdoc />
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        UserInputProcessor.ProcessMouseDown(PhysicalPixel(e.GetPosition(this)), e.GetCurrentPoint(this).Properties.PointerUpdateKind, e.KeyModifiers);
        e.Pointer.Capture(this);
    }

    /// <inheritdoc />
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        UserInputProcessor.ProcessMouseUp(PhysicalPixel(e.GetPosition(this)), e.GetCurrentPoint(this).Properties.PointerUpdateKind, e.KeyModifiers);
        e.Pointer.Capture(null);
    }

    /// <inheritdoc />
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        PointerMoveEvents++;
        UserInputProcessor.ProcessMouseMove(PhysicalPixel(e.GetPosition(this)), e.KeyModifiers);
    }

    /// <inheritdoc />
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        PointerWheelEvents++;
        if (e.Delta.Y != 0) UserInputProcessor.ProcessMouseWheel(PhysicalPixel(e.GetPosition(this)), e.Delta.Y, e.KeyModifiers);
        e.Handled = HandleMouseWheelEvent;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        AutomaticRefresh = false;
        await Gate.WaitAsync();
        try
        {
            if (Disposed) return;
            Disposed = true;
            ElementComposition.SetElementChildVisual(this, null);
            if (Presenter is not null) { await Presenter.DisposeAsync(); Presenter = null; }
            Surface?.Dispose(); Surface = null;
            Visual = null;
            Renderer?.Dispose(); Renderer = null;
        }
        finally { Gate.Release(); }
    }
}
