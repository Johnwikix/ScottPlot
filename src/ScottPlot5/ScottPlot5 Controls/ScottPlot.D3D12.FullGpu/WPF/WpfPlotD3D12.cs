using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ScottPlot.D3D12.Rendering;
using ScottPlot.WPF;
using SkiaSharp;

namespace ScottPlot.D3D12;

/// <summary>Displays complete ScottPlot Figures through hardware D3D12 and WPF D3DImage.</summary>
/// <remarks>All normal Plot, Multiplot, menu and input APIs come from WpfPlotBase. GPU initialization failures are explicit; no CPU drawing control is created.</remarks>
[TemplatePart(Name = "PART_Viewport", Type = typeof(Grid))]
[TemplatePart(Name = "PART_GpuImage", Type = typeof(System.Windows.Controls.Image))]
public class WpfPlotD3D12 : WpfPlotBase, IDisposable
{
    private Grid? Viewport;
    private System.Windows.Controls.Image? Host;
    private D3DImage? Image;
    private D3D12Renderer? Renderer;
    private WpfImagePresenter? Presenter;
    private DispatcherTimer? RecoveryTimer;
    private int RecoveryAttempt;
    private bool RecoveryInitialization;
    private bool FrontBufferRestored;
    private bool Queued;
    private bool Disposed;
    private readonly Action RenderAction;
    /// <inheritdoc />
    public override GRContext GRContext => GetRenderer().Device.Context;
    /// <inheritdoc />
    protected override FrameworkElement PlotFrameworkElement => (FrameworkElement?)Viewport ?? this;
    /// <summary>Gets measurements for the most recent full Figure, excluding WPF composition.</summary>
    public D3D12FrameStatistics LastGpuFrame => Renderer?.LastFrame ?? default;
    /// <summary>Gets whether rendering is suspended while WPF has no front buffer or lost GPU resources are being rebuilt.</summary>
    public bool IsRenderSuspended { get; private set; }

    /// <summary>Captures one explicit GPU-rendered image for export or diagnostics.</summary>
    /// <returns>A caller-owned bitmap of the most recent full Figure.</returns>
    public SKBitmap CaptureBitmap() => GetRenderer().CaptureBitmap();

    static WpfPlotD3D12() => DefaultStyleKeyProperty.OverrideMetadata(typeof(WpfPlotD3D12), new FrameworkPropertyMetadata(typeof(WpfPlotD3D12)));
    /// <summary>Initializes a GPU plot control with the standard ScottPlot input processor and menu.</summary>
    public WpfPlotD3D12()
    {
        RenderAction = RenderFrame; Menu = new WpfD3D12PlotMenu(this);
        Loaded += OnLoaded; Unloaded += OnUnloaded; SizeChanged += OnSizeChanged;
    }
    /// <inheritdoc />
    public override void OnApplyTemplate()
    {
        DetachTemplate();
        base.OnApplyTemplate();
        Viewport = GetTemplateChild("PART_Viewport") as Grid;
        Host = GetTemplateChild("PART_GpuImage") as System.Windows.Controls.Image;
        if (Viewport is null || Host is null) throw new InvalidOperationException("WpfPlotD3D12 requires PART_Viewport and PART_GpuImage in its template.");
        Image = new D3DImage();
        Image.IsFrontBufferAvailableChanged += OnFrontBufferChanged;
        Host.Source = Image;
        Viewport.MouseDown += OnMouseDown; Viewport.MouseUp += OnMouseUp; Viewport.MouseMove += OnMouseMove;
        Viewport.MouseWheel += OnMouseWheel; Viewport.LostMouseCapture += OnLostInput; Viewport.LostKeyboardFocus += OnLostInput;
        Refresh();
    }
    /// <inheritdoc />
    public override void Refresh()
    {
        if (Disposed) return;
        if (!CheckAccess()) { Dispatcher.BeginInvoke(Refresh); return; }
        if (Queued || !IsLoaded || Viewport is null) return;
        Queued = true; Dispatcher.BeginInvoke(DispatcherPriority.Render, RenderAction);
    }
    private D3D12Renderer GetRenderer()
    {
        VerifyAccess(); ObjectDisposedException.ThrowIf(Disposed, this);
        return Renderer ??= new();
    }
    private void RenderFrame()
    {
        Queued = false;
        if (Disposed || !IsLoaded || Viewport is null || Host is null || Image is null) return;
        if (!Image.IsFrontBufferAvailable) { IsRenderSuspended = true; RecoveryTimer?.Stop(); return; }
        if (FrontBufferRestored || Renderer?.Device.IsDeviceLost == true) BeginRecovery(scheduleRetry: false);
        bool initializingRecovery = RecoveryInitialization;
        try
        {
            D3D12Renderer renderer = GetRenderer();
            Presenter ??= new(renderer, Image);
            RecoveryInitialization = false; initializingRecovery = false;
            DpiScale dpi = VisualTreeHelper.GetDpi(Viewport);
            int width = (int)Math.Ceiling(Viewport.ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(Viewport.ActualHeight * dpi.DpiScaleY);
            if (width <= 0 || height <= 0) return;
            DisplayScale = (float)dpi.DpiScaleX;
            renderer.Render(Multiplot, width, height);
            Host.Width = renderer.CapacityWidth / dpi.DpiScaleX; Host.Height = renderer.CapacityHeight / dpi.DpiScaleY;
            IsRenderSuspended = !Presenter.Present(width, height);
            if (!IsRenderSuspended) ResetRecovery();
        }
        catch (Exception ex) when (initializingRecovery || Renderer?.Device.IsDeviceLost == true || !Image.IsFrontBufferAvailable || IsDeviceLoss(ex.HResult))
        {
            BeginRecovery();
        }
    }
    // DXGI/D3D9 device removal, reset, hang and transient display-mode changes.
    private static bool IsDeviceLoss(int hresult) => (uint)hresult is
        0x887A0005 or 0x887A0006 or 0x887A0007 or 0x887A0020 or 0x887A0022 or 0x887A0025 or
        0x88760827 or 0x88760868 or 0x88760869 or 0x88760870;
    private void BeginRecovery(bool scheduleRetry = true)
    {
        FrontBufferRestored = false;
        RecoveryInitialization = true;
        IsRenderSuspended = true;
        ReleaseGpu();
        if (scheduleRetry) ScheduleRecovery();
    }
    private void ScheduleRecovery()
    {
        if (Disposed || !IsLoaded || Image?.IsFrontBufferAvailable != true) return;
        RecoveryTimer ??= new DispatcherTimer();
        RecoveryTimer.Tick -= OnRecoveryTick;
        RecoveryTimer.Tick += OnRecoveryTick;
        RecoveryTimer.Interval = TimeSpan.FromMilliseconds(Math.Min(100 * (1 << Math.Min(RecoveryAttempt++, 4)), 2000));
        RecoveryTimer.Stop(); RecoveryTimer.Start();
    }
    private void OnRecoveryTick(object? sender, EventArgs e)
    {
        RecoveryTimer?.Stop();
        Refresh();
    }
    private void ResetRecovery()
    {
        RecoveryTimer?.Stop(); RecoveryAttempt = 0; RecoveryInitialization = false; FrontBufferRestored = false;
    }
    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();
    private void OnUnloaded(object sender, RoutedEventArgs e) { ResetRecovery(); ReleaseGpu(); }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Refresh();
    private void OnFrontBufferChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (Disposed || Image is null) return;
        IsRenderSuspended = !Image.IsFrontBufferAvailable;
        if (IsRenderSuspended) { RecoveryTimer?.Stop(); return; }
        FrontBufferRestored |= Renderer is not null || Presenter is not null;
        Refresh();
    }
    private void OnLostInput(object sender, RoutedEventArgs e) => UserInputProcessor.ProcessLostFocus();
    private Pixel PixelAt(MouseEventArgs e)
    {
        Point point = e.GetPosition(PlotFrameworkElement); DpiScale dpi = VisualTreeHelper.GetDpi(PlotFrameworkElement);
        return new Pixel((float)(point.X * dpi.DpiScaleX), (float)(point.Y * dpi.DpiScaleY));
    }
    private void SyncModifiers()
    {
        var keys = UserInputProcessor.KeyState;
        keys.Remove(Interactivity.StandardKeys.Control); keys.Remove(Interactivity.StandardKeys.Alt); keys.Remove(Interactivity.StandardKeys.Shift);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) keys.Add(Interactivity.StandardKeys.Control);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) keys.Add(Interactivity.StandardKeys.Alt);
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) keys.Add(Interactivity.StandardKeys.Shift);
    }
    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(this); SyncModifiers(); Pixel pixel = PixelAt(e);
        UserInputProcessor.Process(e.ChangedButton switch {
            MouseButton.Left => new Interactivity.UserActions.LeftMouseDown(pixel),
            MouseButton.Middle => new Interactivity.UserActions.MiddleMouseDown(pixel),
            MouseButton.Right => new Interactivity.UserActions.RightMouseDown(pixel),
            _ => (Interactivity.IUserAction)new Interactivity.UserActions.Unknown(e.ChangedButton.ToString(), "pressed") });
        Viewport?.CaptureMouse();
    }
    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        SyncModifiers(); Pixel pixel = PixelAt(e);
        UserInputProcessor.Process(e.ChangedButton switch {
            MouseButton.Left => new Interactivity.UserActions.LeftMouseUp(pixel),
            MouseButton.Middle => new Interactivity.UserActions.MiddleMouseUp(pixel),
            MouseButton.Right => new Interactivity.UserActions.RightMouseUp(pixel),
            _ => (Interactivity.IUserAction)new Interactivity.UserActions.Unknown(e.ChangedButton.ToString(), "released") });
        Viewport?.ReleaseMouseCapture();
    }
    private void OnMouseMove(object sender, MouseEventArgs e) { SyncModifiers(); UserInputProcessor.Process(new Interactivity.UserActions.MouseMove(PixelAt(e))); }
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SyncModifiers(); Pixel pixel = PixelAt(e);
        UserInputProcessor.Process(e.Delta > 0 ? new Interactivity.UserActions.MouseWheelUp(pixel) : (Interactivity.IUserAction)new Interactivity.UserActions.MouseWheelDown(pixel));
    }
    private void ReleaseGpu()
    {
        WpfImagePresenter? presenter = Presenter;
        D3D12Renderer? renderer = Renderer;
        Presenter = null; Renderer = null;
        bool recovering = RecoveryInitialization || renderer?.Device.IsDeviceLost == true || Image?.IsFrontBufferAvailable == false;
        try { presenter?.Dispose(); }
        catch (Exception) when (recovering) { }
        finally
        {
            try { renderer?.Dispose(); }
            catch (Exception) when (recovering) { }
        }
    }
    private void DetachTemplate()
    {
        ResetRecovery();
        ReleaseGpu();
        if (Host is not null) Host.Source = null;
        if (Image is not null) Image.IsFrontBufferAvailableChanged -= OnFrontBufferChanged;
        Image = null;
        if (Viewport is not null)
        {
            Viewport.MouseDown -= OnMouseDown; Viewport.MouseUp -= OnMouseUp; Viewport.MouseMove -= OnMouseMove;
            Viewport.MouseWheel -= OnMouseWheel; Viewport.LostMouseCapture -= OnLostInput; Viewport.LostKeyboardFocus -= OnLostInput;
        }
        Viewport = null; Host = null;
    }
    /// <inheritdoc />
    public void Dispose()
    {
        if (Disposed) return;
        VerifyAccess(); DetachTemplate(); Disposed = true;
        Loaded -= OnLoaded; Unloaded -= OnUnloaded; SizeChanged -= OnSizeChanged;
        GC.SuppressFinalize(this);
    }
}



