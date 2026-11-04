using System.ComponentModel;
using ScottPlot.D3D12.Rendering;
using ScottPlot.WinForms;
using SkiaSharp;

namespace ScottPlot.D3D12;

/// <summary>Displays complete ScottPlot Figures using hardware D3D12 and a native WinForms window swapchain.</summary>
/// <remarks>
/// Inherits the standard Plot, Multiplot, Reset and input APIs. Client sizes and mouse positions use physical pixels.
/// Create, render, capture and dispose on the UI thread. Refresh may be called from another thread after handle creation.
/// GPU failures propagate without a CPU fallback. Handle recreation releases and lazily recreates GPU resources.
/// </remarks>
[ToolboxItem(true)]
public class FormsPlotD3D12 : FormsPlotBase
{
    private readonly int OwnerThread = Environment.CurrentManagedThreadId;
    private readonly Action RefreshAction;
    private D3D12Renderer? Renderer;
    private D3D12HwndPresenter? Presenter;
    private int RefreshQueued;
    private bool GpuDisposed;

    /// <inheritdoc/>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public override GRContext GRContext => GetRenderer().Device.Context;

    /// <summary>Gets measurements for the last complete GPU Figure, excluding swapchain presentation.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public D3D12FrameStatistics LastGpuFrame => Renderer?.LastFrame ?? default;

    /// <summary>Gets the number of submitted presentation frames for the current window handle.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public long PresentedFrames => Presenter?.PresentedFrames ?? 0;

    /// <summary>Gets the number of explicit captures for the current renderer; ordinary rendering leaves this unchanged.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public long PixelReadbackCount => Renderer?.PixelReadbackCount ?? 0;

    /// <summary>Gets the number of swapchain creations and buffer resizes for the current window handle.</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden), Browsable(false)]
    public int SwapChainResizeCount => Presenter?.ResizeCount ?? 0;

    /// <summary>Gets or sets a value indicating whether presentation is synchronized to the display.</summary>
    [DefaultValue(true)]
    public bool SynchronizeToDisplay { get; set; } = true;

    /// <summary>Initializes a new instance of the <see cref="FormsPlotD3D12"/> class.</summary>
    public FormsPlotD3D12()
    {
        RefreshAction = InvalidateOnUiThread;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque | ControlStyles.Selectable, true);
        SetStyle(ControlStyles.OptimizedDoubleBuffer, false);
        TabStop = true;
        Menu = new FormsD3D12PlotMenu(this);
        if (!IsDesignerAlternative)
        {
            Plot.FigureBackground.Color = Color.FromColor(System.Drawing.SystemColors.Control);
            Plot.DataBackground.Color = Colors.White;
        }
    }

    private bool InDesigner => IsDesignerAlternative || DesignMode || LicenseManager.UsageMode == LicenseUsageMode.Designtime;

    /// <summary>Queues a repaint on the UI thread, coalescing background refresh requests.</summary>
    /// <remarks>Requests made before handle creation or after disposal are ignored. This does not synchronize mutations of Plot data.</remarks>
    public override void Refresh()
    {
        if (GpuDisposed || IsDisposed || Disposing || !IsHandleCreated)
            return;

        if (Environment.CurrentManagedThreadId == OwnerThread)
        {
            Invalidate();
            return;
        }

        if (Interlocked.Exchange(ref RefreshQueued, 1) != 0)
            return;
        try
        {
            BeginInvoke(RefreshAction);
        }
        catch (InvalidOperationException)
        {
            // BeginInvoke can race with handle replacement, including a replacement completed before this catch.
            Interlocked.Exchange(ref RefreshQueued, 0);
        }
    }

    private void InvalidateOnUiThread()
    {
        Interlocked.Exchange(ref RefreshQueued, 0);
        if (!GpuDisposed && !IsDisposed && IsHandleCreated)
            Invalidate();
    }

    private void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != OwnerThread)
            throw new InvalidOperationException("Access the D3D12 control on its owning UI thread.");
        ObjectDisposedException.ThrowIf(GpuDisposed || IsDisposed, this);
    }

    private D3D12Renderer GetRenderer()
    {
        VerifyAccess();
        if (InDesigner)
            throw new InvalidOperationException("D3D12 rendering is unavailable in the WinForms designer.");
        return Renderer ??= new();
    }

    /// <summary>Captures the last completed GPU Figure with one explicit GPU-to-CPU transfer.</summary>
    /// <returns>A bitmap that the caller must dispose.</returns>
    /// <exception cref="InvalidOperationException">There is no completed frame, or the caller is on another thread.</exception>
    public SKBitmap CaptureBitmap() => GetRenderer().CaptureBitmap();

    /// <inheritdoc/>
    protected override void OnPaint(PaintEventArgs e)
    {
        if (GpuDisposed || InDesigner || !Visible || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            return;

        D3D12Renderer renderer = GetRenderer();
        Presenter ??= new(renderer, Handle);
        DisplayScale = DeviceDpi / 96f;
        renderer.Render(Multiplot, ClientSize.Width, ClientSize.Height);
        Presenter.Present(ClientSize.Width, ClientSize.Height, SynchronizeToDisplay);
        base.OnPaint(e);
    }

    /// <inheritdoc/>
    protected override void OnPaintBackground(PaintEventArgs e) { }

    /// <inheritdoc/>
    protected override void OnHandleDestroyed(EventArgs e)
    {
        try { ReleaseGpu(); }
        finally
        {
            Interlocked.Exchange(ref RefreshQueued, 0);
            base.OnHandleDestroyed(e);
        }
    }

    /// <inheritdoc/>
    protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); Refresh(); }

    /// <inheritdoc/>
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Refresh(); }

    /// <inheritdoc/>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        Focus();
        Capture = true;
        UserInputProcessor.ProcessMouseDown(e);
        base.OnMouseDown(e);
    }

    /// <inheritdoc/>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        UserInputProcessor.ProcessMouseUp(e);
        if (MouseButtons == MouseButtons.None)
            Capture = false;
        base.OnMouseUp(e);
    }

    /// <inheritdoc/>
    protected override void OnMouseMove(MouseEventArgs e) { UserInputProcessor.ProcessMouseMove(e); base.OnMouseMove(e); }

    /// <inheritdoc/>
    protected override void OnMouseWheel(MouseEventArgs e) { UserInputProcessor.ProcessMouseWheel(e); base.OnMouseWheel(e); }

    /// <inheritdoc/>
    protected override void OnKeyDown(KeyEventArgs e) { UserInputProcessor.ProcessKeyDown(e); base.OnKeyDown(e); }

    /// <inheritdoc/>
    protected override void OnKeyUp(KeyEventArgs e) { UserInputProcessor.ProcessKeyUp(e); base.OnKeyUp(e); }

    /// <inheritdoc/>
    protected override void OnPreviewKeyDown(PreviewKeyDownEventArgs e)
    {
        if (e.KeyCode is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
            e.IsInputKey = true;
        base.OnPreviewKeyDown(e);
    }

    /// <inheritdoc/>
    protected override void OnLostFocus(EventArgs e) { UserInputProcessor.ProcessLostFocus(); base.OnLostFocus(e); }

    /// <inheritdoc/>
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        if (!Capture)
            UserInputProcessor.ProcessLostFocus();
        base.OnMouseCaptureChanged(e);
    }

    private void ReleaseGpu()
    {
        try { Presenter?.Dispose(); }
        finally
        {
            Presenter = null;
            try { Renderer?.Dispose(); }
            finally { Renderer = null; }
        }
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !GpuDisposed)
        {
            VerifyAccess();
            GpuDisposed = true;
            try { ReleaseGpu(); }
            finally { base.Dispose(disposing); }
            return;
        }
        base.Dispose(disposing);
    }
}
