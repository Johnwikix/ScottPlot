using Avalonia;
using Avalonia.Skia;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using SkiaSharp;

using Controls = Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace ScottPlot.Avalonia;

public class AvaPlot : Controls.Control, IPlotControl, IAsyncDisposable
{
    /// <summary>Defines the explicitly requested rendering path.</summary>
    public static readonly StyledProperty<AvaPlotRenderBackend> RenderBackendProperty =
        AvaloniaProperty.Register<AvaPlot, AvaPlotRenderBackend>(nameof(RenderBackend));

    /// <summary>Defines the latched initialization or presentation error.</summary>
    public static readonly DirectProperty<AvaPlot, Exception?> RenderExceptionProperty =
        AvaloniaProperty.RegisterDirect<AvaPlot, Exception?>(nameof(RenderException), control => control.RenderException);

    private readonly SemaphoreSlim RenderGate = new(1);
    private CancellationTokenSource? Lifetime;
    private TopLevel? AttachedTopLevel;
    private IAvaPlotRenderer? Renderer;
    private Exception? LastError;
    private int Generation;
    private bool RefreshQueued;
    private bool RefreshPending;
    private bool Disposed;
    private Task? DisposalTask;

    /// <summary>Gets or sets the requested backend. PlatformSkia is the cross-platform default.</summary>
    public AvaPlotRenderBackend RenderBackend
    {
        get => GetValue(RenderBackendProperty);
        set => SetValue(RenderBackendProperty, value);
    }

    /// <summary>Gets the last backend failure, cleared by changing backend or explicitly retrying.</summary>
    public Exception? RenderException => LastError;

    /// <summary>Gets the borrowed optional renderer for backend-specific diagnostics; callers must not dispose it.</summary>
    public IAvaPlotRenderer? RenderBackendInstance => Renderer;

    /// <summary>Occurs once when an initialization or presentation failure is latched. No fallback occurs.</summary>
    public event EventHandler<AvaPlotRenderFailedEventArgs>? RenderFailed;

    public Plot Plot { get; internal set; }
    public IMultiplot Multiplot { get; set; }
    public IPlotMenu? Menu { get; set; }
    public Interactivity.UserInputProcessor UserInputProcessor { get; }

    public GRContext? GRContext => Renderer?.GRContext;

    /// <summary>
    /// Prevent mouse wheel events from bubbling up to parent controls (e.g., ScrollViewer)
    /// </summary>
    public bool HandleMouseWheelEvent { get; set; } = true;

    public float DisplayScale { get; set; }

    public AvaPlot()
    {
        Plot = new() { PlotControl = this };
        Multiplot = new Multiplot(Plot);
        ClipToBounds = true;
        DisplayScale = DetectDisplayScale();
        UserInputProcessor = new(this);
        Menu = new AvaPlotMenu(this);
        Focusable = true; // Required for keyboard events
        Refresh();
    }

    private class CustomDrawOp : ICustomDrawOperation
    {
        private readonly IMultiplot Multiplot;

        public Rect Bounds { get; }
        public bool HitTest(Point p) => true;
        public bool Equals(ICustomDrawOperation? other) => false;

        public CustomDrawOp(Rect bounds, IMultiplot multiplot)
        {
            Multiplot = multiplot;
            Bounds = bounds;
        }

        public void Dispose()
        {
            // No-op
        }

        public void Render(ImmediateDrawingContext context)
        {
            var leaseFeature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>();
            if (leaseFeature is null) return;

            using var lease = leaseFeature.Lease();
            PixelRect rect = new(0, (float)Bounds.Width, (float)Bounds.Height, 0);

            using SKAutoCanvasRestore _ = new(lease.SkCanvas, false);
            lease.SkCanvas.SaveLayer();
            Multiplot.Render(lease.SkCanvas, rect);
        }
    }

    public override void Render(DrawingContext context)
    {
        if (RenderException is { } error)
        {
            context.DrawRectangle(Brushes.MistyRose, null, new Rect(Bounds.Size));
            var message = new FormattedText($"{RenderBackend} rendering failed: {error.Message}",
                CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, 14, Brushes.DarkRed)
            { MaxTextWidth = Math.Max(1, Bounds.Width - 16) };
            context.DrawText(message, new Point(8, 8));
            return;
        }
        if (RenderBackend != AvaPlotRenderBackend.PlatformSkia) return;
        Rect controlBounds = new(Bounds.Size);
        CustomDrawOp customDrawOp = new(controlBounds, Multiplot);
        context.Custom(customDrawOp);
    }

    public void Reset()
    {
        Plot plot = new() { PlotControl = this };
        Reset(plot);
    }

    public void Reset(Plot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);
        if (ReferenceEquals(Plot, plot)) return;
        plot.PlotControl = this;
        Plot oldPlot = Plot;
        Plot = plot;
        oldPlot?.Dispose();
        Multiplot.Reset(plot);
    }

    public void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Background); return; }
        if (Disposed || RenderException is not null) return;
        if (RefreshQueued) { RefreshPending = true; return; }
        RefreshQueued = true;
        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                do
                {
                    RefreshPending = false;
                    if (Disposed || RenderException is not null) return;
                    await RefreshAsync();
                }
                while (RefreshPending);
            }
            catch { /* RefreshAsync reports the error through RenderException, RenderFailed and the error surface. */ }
            finally { RefreshQueued = false; }
        }, DispatcherPriority.Background);
    }

    /// <summary>Renders a complete frame and propagates initialization or presentation errors to the caller.</summary>
    /// <returns>A task completing at the frame's composition boundary, or immediately while detached or zero-sized.</returns>
    public async Task RefreshAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            await Dispatcher.UIThread.InvokeAsync(RefreshAsync);
            return;
        }
        ObjectDisposedException.ThrowIf(Disposed, this);
        int generation = Generation;
        await RenderGate.WaitAsync();
        try
        {
            if (generation != Generation || AttachedTopLevel is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
            if (RenderException is not null) throw new InvalidOperationException("The selected rendering backend has failed. Call RetryRenderAsync() or change RenderBackend.", RenderException);
            CancellationToken token = Lifetime!.Token;
            if (RenderBackend == AvaPlotRenderBackend.PlatformSkia)
            {
                InvalidateVisual();
                var compositor = ElementComposition.GetElementVisual(this)?.Compositor;
                if (compositor is not null) await compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(token);
            }
            else if (RenderBackend == AvaPlotRenderBackend.Direct3D12)
            {
                Renderer ??= AvaPlotRenderBackendRegistry.CreateDirect3D12();
                await Renderer.RenderAsync(this, token);
            }
            else throw new NotSupportedException($"Unsupported AvaPlot backend: {RenderBackend}.");
        }
        catch (OperationCanceledException) when (generation != Generation) { }
        catch (Exception error)
        {
            if (generation == Generation && RenderException is null) ReportFailure(error);
            throw;
        }
        finally { RenderGate.Release(); }
    }

    /// <summary>Releases the failed renderer, clears its error and explicitly attempts initialization again.</summary>
    /// <returns>A task completing after the replacement renderer presents a frame.</returns>
    public async Task RetryRenderAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess()) { await Dispatcher.UIThread.InvokeAsync(RetryRenderAsync); return; }
        ObjectDisposedException.ThrowIf(Disposed, this);
        ResetLifetime();
        await ReleaseRendererAsync();
        SetAndRaise(RenderExceptionProperty, ref LastError, null);
        await RefreshAsync();
    }

    /// <inheritdoc />
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Disposed) return;
        AttachedTopLevel = TopLevel.GetTopLevel(this);
        if (AttachedTopLevel is not null) AttachedTopLevel.ScalingChanged += OnScalingChanged;
        ResetLifetime();
        DisplayScale = DetectDisplayScale();
        Refresh();
    }

    /// <inheritdoc />
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (AttachedTopLevel is not null) AttachedTopLevel.ScalingChanged -= OnScalingChanged;
        AttachedTopLevel = null;
        ResetLifetime();
        ObserveRelease(Generation);
        UserInputProcessor.ProcessLostFocus();
        base.OnDetachedFromVisualTree(e);
    }

    /// <inheritdoc />
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == RenderBackendProperty)
        {
            ResetLifetime();
            SetAndRaise(RenderExceptionProperty, ref LastError, null);
            ObserveRelease(Generation);
            InvalidateVisual();
            Refresh();
        }
        else if (change.Property == BoundsProperty) Refresh();
    }

    private void OnScalingChanged(object? sender, EventArgs args)
    {
        DisplayScale = DetectDisplayScale();
        Refresh();
    }

    private void ResetLifetime()
    {
        Generation++;
        Lifetime?.Cancel(); Lifetime?.Dispose();
        Lifetime = AttachedTopLevel is null || Disposed ? null : new CancellationTokenSource();
    }

    private async void ObserveRelease(int generation)
    {
        try { await ReleaseRendererAsync(); }
        catch (Exception error) { if (generation == Generation) ReportFailure(error); }
    }

    private async Task ReleaseRendererAsync()
    {
        await RenderGate.WaitAsync();
        try
        {
            var renderer = Renderer; Renderer = null;
            if (renderer is not null) await renderer.DisposeAsync();
        }
        finally { RenderGate.Release(); }
    }

    private void ReportFailure(Exception error)
    {
        ElementComposition.SetElementChildVisual(this, null);
        SetAndRaise(RenderExceptionProperty, ref LastError, error);
        InvalidateVisual();
        RenderFailed?.Invoke(this, new(RenderBackend, error));
    }

    /// <summary>Permanently releases this control's renderer and Plot on its UI thread.</summary>
    /// <returns>A task completing after outstanding presentation and resource disposal finish.</returns>
    public ValueTask DisposeAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess())
            return new ValueTask(Dispatcher.UIThread.InvokeAsync(async () => await DisposeAsync()));
        return new ValueTask(DisposalTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        Disposed = true;
        if (AttachedTopLevel is not null) AttachedTopLevel.ScalingChanged -= OnScalingChanged;
        ResetLifetime();
        await ReleaseRendererAsync();
        Plot.Dispose();
    }

    public void ShowContextMenu(Pixel position)
    {
        Menu?.ShowContextMenu(position);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        Focus();
        Pixel pixel = e.ToPixel(this);
        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        UserInputProcessor.ProcessMouseDown(pixel, kind, e.KeyModifiers);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        Pixel pixel = e.ToPixel(this);
        PointerUpdateKind kind = e.GetCurrentPoint(this).Properties.PointerUpdateKind;
        UserInputProcessor.ProcessMouseUp(pixel, kind, e.KeyModifiers);

        e.Pointer.Capture(null);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        Pixel pixel = e.ToPixel(this);
        UserInputProcessor.ProcessMouseMove(pixel, e.KeyModifiers);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Pixel pixel = e.ToPixel(this);
        float delta = (float)e.Delta.Y; // This is now the correct behavior even if shift is held, see https://github.com/AvaloniaUI/Avalonia/pull/8628

        if (delta != 0)
        {
            UserInputProcessor.ProcessMouseWheel(pixel, delta, e.KeyModifiers);
        }

        e.Handled = HandleMouseWheelEvent;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        UserInputProcessor.ProcessKeyDown(e);
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        UserInputProcessor.ProcessKeyUp(e);
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        UserInputProcessor.ProcessLostFocus();
    }

    public float DetectDisplayScale()
    {
        return (float)(TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
    }

    public void SetCursor(Cursor cursor)
    {
        Cursor = cursor.GetCursor();
    }
}
