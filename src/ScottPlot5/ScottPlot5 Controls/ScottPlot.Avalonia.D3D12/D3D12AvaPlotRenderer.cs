using Avalonia;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using ScottPlot.D3D12.Rendering;
using SkiaSharp;

namespace ScottPlot.Avalonia.D3D12;

/// <summary>Renders ordinary AvaPlot models with hardware D3D12 and Avalonia GPU composition.</summary>
/// <remarks>Uses logical plot coordinates and a compositor-matched device. GPU resources are rebuilt after device loss while the control is attached.</remarks>
public sealed class D3D12AvaPlotRenderer : IAvaPlotRenderer
{
    private AvaPlot? Owner;
    private Compositor? Compositor;
    private CompositionDrawingSurface? Surface;
    private CompositionSurfaceVisual? Visual;
    private D3D12DeviceLease? Lease;
    private D3D12Renderer? Renderer;
    private Internal.SharedTexturePresenter? Presenter;
    private bool Disposed;

    /// <summary>Gets the borrowed hardware Direct3D context, or null before initialization.</summary>
    public GRContext? GRContext => Renderer?.Device.Context;

    /// <summary>Gets or sets whether the existing equivalent SignalXY sampling and path optimizations are enabled.</summary>
    public bool OptimizeSignals { get; set; } = true;

    /// <summary>Gets statistics for the last full Figure, through its D3D12 resolve and GPU fence.</summary>
    public D3D12FrameStatistics LastFrame => Renderer?.LastFrame ?? default;

    /// <summary>Gets the number of completed full-Figure presentation batches.</summary>
    public long CompletedFrames { get; private set; }

    /// <summary>Gets the number of explicit diagnostic captures; normal rendering keeps this zero.</summary>
    public long PixelReadbackCount => Renderer?.PixelReadbackCount ?? 0;

    /// <summary>Gets the shared presentation texture allocation count.</summary>
    public int SharedTextureAllocations => Presenter?.TextureAllocations ?? 0;

    /// <summary>Gets the number of GPU copies submitted to the shared presentation texture.</summary>
    public long PresentationCopies => Presenter?.Copies ?? 0;

    /// <summary>Gets the hardware adapter name.</summary>
    public string? AdapterName => Renderer?.Device.AdapterName;

    /// <summary>Gets the retained GPU target width, which may exceed the viewport after resizing.</summary>
    public int CapacityWidth => Renderer?.CapacityWidth ?? 0;

    /// <summary>Gets the retained GPU target height, which may exceed the viewport after resizing.</summary>
    public int CapacityHeight => Renderer?.CapacityHeight ?? 0;

    /// <inheritdoc />
    public async Task RenderAsync(AvaPlot control, CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(Disposed, this);
        ArgumentNullException.ThrowIfNull(control);
        cancellationToken.ThrowIfCancellationRequested();
        if (Owner is not null && !ReferenceEquals(Owner, control)) throw new InvalidOperationException("A renderer cannot be shared between controls.");
        Owner = control;
        for (int index = 0; index < control.Multiplot.Subplots.Count; index++)
            if (control.Multiplot.Subplots.GetPlot(index).PlottableList.Any(p => p is IPlottableGL))
                throw new NotSupportedException("OpenGL-only plottables cannot render on the Direct3D12 backend.");
        for (int attempt = 0; ; attempt = Math.Min(attempt + 1, 4))
        {
            cancellationToken.ThrowIfCancellationRequested();
            double scale = TopLevel.GetTopLevel(control)?.RenderScaling
                ?? throw new InvalidOperationException("The control no longer belongs to a window.");
            var size = new global::Avalonia.PixelSize(checked((int)Math.Ceiling(control.Bounds.Width * scale)),
                checked((int)Math.Ceiling(control.Bounds.Height * scale)));
            if (size.Width <= 0 || size.Height <= 0) return;

            var visual = ElementComposition.GetElementVisual(control)
                ?? throw new InvalidOperationException("Attach AvaPlot to a window before initializing D3D12.");
            bool initializing = Renderer is null;
            try
            {
                if (initializing) await InitializeAsync(visual.Compositor, cancellationToken);
                if (!ReferenceEquals(Compositor, visual.Compositor))
                    throw new InvalidOperationException("The Avalonia compositor changed.");
                await RenderFrameAsync(control, size, cancellationToken);
                return;
            }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested &&
                (Renderer?.Device.IsDeviceLost == true || Presenter?.IsDeviceLost == true ||
                 (Compositor is not null && !ReferenceEquals(Compositor, visual.Compositor)) ||
                 D3D12GraphicsDevice.IsDeviceLoss(error) || (initializing && attempt > 0)))
            {
                try { await ReleaseGpuAsync(); }
                catch (Exception) { /* The lost resources cannot be reused. Continue rebuilding. */ }
                await Task.Delay(100 << attempt, cancellationToken);
            }
        }
    }

    private async Task RenderFrameAsync(AvaPlot control, global::Avalonia.PixelSize size, CancellationToken cancellationToken)
    {
        Presenter!.VerifyDevice();
        Visual!.Size = new(control.Bounds.Width, control.Bounds.Height);
        Renderer!.OptimizeSignals = OptimizeSignals;
        long reads = PixelReadbackCount;
        Renderer.Render(control.Multiplot, size.Width, size.Height,
            new PixelSize((float)control.Bounds.Width, (float)control.Bounds.Height));
        await Presenter.PresentAsync(size, cancellationToken);
        Renderer.Device.VerifyAccess();
        Presenter.VerifyDevice();
        if (PixelReadbackCount != reads) throw new InvalidOperationException("Unexpected CPU pixel readback in the presentation loop.");
        CompletedFrames++;
    }

    private async Task InitializeAsync(Compositor compositor, CancellationToken token)
    {
        var interop = await compositor.TryGetCompositionGpuInterop().AsTask().WaitAsync(token);
        token.ThrowIfCancellationRequested();
        if (interop?.IsLost == true)
            throw new System.Runtime.InteropServices.COMException("Avalonia's compositor GPU context was lost.", unchecked((int)0x887A0005));
        if (interop is null || interop.DeviceLuid is not { Length: 8 } luid)
            throw new PlatformNotSupportedException("Avalonia's current GPU backend does not expose D3D11 image interop and an adapter LUID. Select an ANGLE backend supporting shared textures.");
        D3D12DeviceLease? lease = null;
        D3D12Renderer? renderer = null;
        CompositionDrawingSurface? surface = null;
        Internal.SharedTexturePresenter? presenter = null;
        try
        {
            lease = D3D12GraphicsDevice.AcquireShared(BitConverter.ToInt64(luid));
            renderer = new(lease.Device);
            surface = compositor.CreateDrawingSurface();
            presenter = new(renderer, interop, surface);
            var visual = compositor.CreateSurfaceVisual();
            visual.Surface = surface;
            token.ThrowIfCancellationRequested();
            ElementComposition.SetElementChildVisual(Owner!, visual);
            Lease = lease; Renderer = renderer; Surface = surface; Presenter = presenter;
            Visual = visual; Compositor = compositor;
            lease.Device.DeviceRemoved += OnDeviceRemoved;
        }
        catch
        {
            try { if (presenter is not null) await presenter.DisposeAsync(); }
            finally
            {
                try { surface?.Dispose(); }
                finally { try { renderer?.Dispose(); } finally { lease?.Dispose(); } }
            }
            throw;
        }
    }

    /// <summary>Captures the current resolved GPU image for an explicit one-shot export or diagnostic.</summary>
    /// <returns>A bitmap owned by the caller. This method performs one pixel readback.</returns>
    public SKBitmap CaptureBitmap()
    {
        Dispatcher.UIThread.VerifyAccess();
        ObjectDisposedException.ThrowIf(Disposed, this);
        return (Renderer ?? throw new InvalidOperationException("Render a frame before capturing it.")).CaptureBitmap();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Dispatcher.UIThread.VerifyAccess();
        if (Disposed) return;
        Disposed = true;
        await ReleaseGpuAsync();
    }

    private void OnDeviceRemoved() => Owner?.Refresh();

    private async ValueTask ReleaseGpuAsync()
    {
        var renderer = Renderer;
        var lease = Lease;
        var surface = Surface;
        var presenter = Presenter;
        Renderer = null; Lease = null; Surface = null; Presenter = null; Visual = null; Compositor = null;
        if (lease is not null) lease.Device.DeviceRemoved -= OnDeviceRemoved;
        try
        {
            try { if (Owner is not null) ElementComposition.SetElementChildVisual(Owner, null); }
            finally { if (presenter is not null) await presenter.DisposeAsync(); }
        }
        finally
        {
            try { surface?.Dispose(); }
            finally { try { renderer?.Dispose(); } finally { lease?.Dispose(); } }
        }
    }
}
