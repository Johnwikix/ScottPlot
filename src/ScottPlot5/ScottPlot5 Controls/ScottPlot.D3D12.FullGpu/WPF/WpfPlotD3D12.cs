using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
    private D3D12Renderer? Renderer;
    private WpfImagePresenter? Presenter;
    private bool Queued;
    private bool Disposed;
    private readonly Action RenderAction;
    /// <inheritdoc />
    public override GRContext GRContext => GetRenderer().Device.Context;
    /// <inheritdoc />
    protected override FrameworkElement PlotFrameworkElement => (FrameworkElement?)Viewport ?? this;
    /// <summary>Gets measurements for the most recent full Figure, excluding WPF composition.</summary>
    public D3D12FrameStatistics LastGpuFrame => Renderer?.LastFrame ?? default;
    /// <summary>Gets whether WPF temporarily has no front buffer; rendering resumes without CPU fallback.</summary>
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
        if (Disposed || !IsLoaded || Viewport is null || Host is null) return;
        DpiScale dpi = VisualTreeHelper.GetDpi(Viewport);
        int width = (int)Math.Ceiling(Viewport.ActualWidth * dpi.DpiScaleX), height = (int)Math.Ceiling(Viewport.ActualHeight * dpi.DpiScaleY);
        if (width <= 0 || height <= 0) return;
        D3D12Renderer renderer = GetRenderer();
        if (Presenter is null)
        {
            Presenter = new(renderer); Presenter.Image.IsFrontBufferAvailableChanged += OnFrontBufferChanged; Host.Source = Presenter.Image;
        }
        IsRenderSuspended = !Presenter.Image.IsFrontBufferAvailable;
        if (IsRenderSuspended) return;
        DisplayScale = (float)dpi.DpiScaleX;
        renderer.Render(Multiplot, width, height);
        Host.Width = renderer.CapacityWidth / dpi.DpiScaleX; Host.Height = renderer.CapacityHeight / dpi.DpiScaleY;
        IsRenderSuspended = !Presenter.Present(width, height);
    }
    private void OnLoaded(object sender, RoutedEventArgs e) => Refresh();
    private void OnUnloaded(object sender, RoutedEventArgs e) => ReleaseGpu();
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => Refresh();
    private void OnFrontBufferChanged(object sender, DependencyPropertyChangedEventArgs e) => Refresh();
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
        if (Host is not null) Host.Source = null;
        if (Presenter is not null) { Presenter.Image.IsFrontBufferAvailableChanged -= OnFrontBufferChanged; Presenter.Dispose(); Presenter = null; }
        Renderer?.Dispose(); Renderer = null;
    }
    private void DetachTemplate()
    {
        ReleaseGpu();
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



