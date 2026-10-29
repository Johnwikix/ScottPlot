using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using ScottPlot.D3D12.Rendering;
using SkiaSharp;

namespace ScottPlot.D3D12.WinUI;

/// <summary>Displays normal ScottPlot Figures on a WinUI 3 native D3D12 SwapChainPanel.</summary>
/// <remarks>Physical pixel allocation and inverse composition-scale transforms are applied exactly once. No SKXamlCanvas or CPU surface is created.</remarks>
public partial class WinUIPlotD3D12 : UserControl, IPlotControl, IDisposable
{
    private readonly SwapChainPanel Panel = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueue DispatcherOwner;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueHandler RenderCallback;
    private D3D12Renderer? Renderer;
    private D3D12SwapChainPresenter? Presenter;
    private XamlRoot? ObservedRoot;
    private int Queued;
    private volatile bool Disposed;
    private float UserScaleRatio = 1;
    /// <inheritdoc />
    public Plot Plot { get; private set; }
    /// <inheritdoc />
    public IMultiplot Multiplot { get; set; }
    /// <inheritdoc />
    public GRContext GRContext => GetRenderer().Device.Context;
    /// <inheritdoc />
    public IPlotMenu? Menu { get; set; }
    /// <inheritdoc />
    public Interactivity.UserInputProcessor UserInputProcessor { get; }
    /// <summary>Gets or sets the owning desktop window used by the native file picker.</summary>
    public Window? AppWindow { get; set; }
    /// <inheritdoc />
    public float DisplayScale { get; set; } = 1;
    /// <summary>Gets statistics for the last completed full Figure, excluding Present/composition.</summary>
    public D3D12FrameStatistics LastGpuFrame => Renderer?.LastFrame ?? default;
    /// <summary>Gets the number of native swapchain resize operations.</summary>
    public int SwapChainResizeCount => Presenter?.ResizeCount ?? 0;
    /// <summary>Gets the number of explicit caller-requested GPU pixel captures.</summary>
    public long PixelReadbackCount => Renderer?.PixelReadbackCount ?? 0;

    /// <summary>Initializes the native GPU plot with standard ScottPlot input, layouts and menus.</summary>
    public WinUIPlotD3D12()
    {
        DispatcherOwner = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() ?? throw new InvalidOperationException("Create the plot on a WinUI UI thread.");
        RenderCallback = RenderFrame;
        Plot = new() { PlotControl = this }; Multiplot = new Multiplot(Plot);
        UserInputProcessor = new(this); Menu = new WinUIPlotMenu(this);
        Background = new SolidColorBrush(Microsoft.UI.Colors.White);
        Panel.HorizontalAlignment = Microsoft.UI.Xaml.HorizontalAlignment.Stretch; Panel.VerticalAlignment = Microsoft.UI.Xaml.VerticalAlignment.Stretch;
        Panel.PointerPressed += OnPointerPressed; Panel.PointerReleased += OnPointerReleased; Panel.PointerMoved += OnPointerMoved;
        Panel.PointerWheelChanged += OnPointerWheelChanged; Panel.KeyDown += OnPanelKeyDown; Panel.KeyUp += OnPanelKeyUp;
        Panel.PointerCaptureLost += OnCaptureLost; Panel.LostFocus += OnLostFocus;
        Panel.SizeChanged += OnPanelSizeChanged; Panel.CompositionScaleChanged += OnCompositionScaleChanged;
        Loaded += OnLoaded; Unloaded += OnUnloaded; Content = Panel; IsTabStop = true;
    }

    /// <inheritdoc />
    public void Refresh()
    {
        // 缓存委托并合并刷新：WinUI .NET 8/10 均不为每次 Refresh 创建闭包或临时图像。
        if (Disposed || Interlocked.CompareExchange(ref Queued, 1, 0) != 0) return;
        if (!DispatcherOwner.TryEnqueue(RenderCallback)) Interlocked.Exchange(ref Queued, 0);
    }
    private void RenderFrame()
    {
        Interlocked.Exchange(ref Queued, 0);
        if (Disposed || !IsLoaded || XamlRoot is null) return;
        int width = (int)Math.Ceiling(Panel.ActualWidth * Panel.CompositionScaleX);
        int height = (int)Math.Ceiling(Panel.ActualHeight * Panel.CompositionScaleY);
        if (width <= 0 || height <= 0) return;
        D3D12Renderer renderer = GetRenderer();
        if (Presenter is null)
        {
            Guid iid = new("63aad0b8-7c24-40ff-85a8-640d944cc325");
            nint inspectable = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(Panel);
            try
            {
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in iid, out nint native));
                try { Presenter = new(renderer, native); } finally { Marshal.Release(native); }
            }
            finally { Marshal.Release(inspectable); }
        }
        renderer.Render(Multiplot, width, height);
        Presenter.Present(width, height, Panel.CompositionScaleX, Panel.CompositionScaleY);
    }
    private D3D12Renderer GetRenderer()
    {
        if (!DispatcherOwner.HasThreadAccess) throw new InvalidOperationException("Access GPU resources on the owning UI thread.");
        ObjectDisposedException.ThrowIf(Disposed, this); return Renderer ??= new();
    }
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ObserveRoot(); DisplayScale = (float)(XamlRoot?.RasterizationScale ?? 1);
        Plot.ScaleFactor = DisplayScale * UserScaleRatio; Refresh();
    }
    private void ObserveRoot()
    {
        if (ReferenceEquals(ObservedRoot, XamlRoot)) return;
        if (ObservedRoot is not null) ObservedRoot.Changed -= OnRootChanged;
        ObservedRoot = XamlRoot;
        if (ObservedRoot is not null) ObservedRoot.Changed += OnRootChanged;
    }
    private void OnRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => DetectDisplayScale();
    private void OnCompositionScaleChanged(SwapChainPanel sender, object args) => Refresh();
    private void OnPanelSizeChanged(object sender, SizeChangedEventArgs args) => Refresh();
    private void OnUnloaded(object sender, RoutedEventArgs args) { if (ObservedRoot is not null) ObservedRoot.Changed -= OnRootChanged; ObservedRoot = null; ReleaseGpu(); }
    /// <inheritdoc />
    public float DetectDisplayScale()
    {
        if (XamlRoot is null) return DisplayScale;
        if (DisplayScale > 0 && Math.Abs(Plot.ScaleFactor - DisplayScale * UserScaleRatio) > .001)
            UserScaleRatio = (float)Plot.ScaleFactor / DisplayScale;
        DisplayScale = (float)XamlRoot.RasterizationScale; Plot.ScaleFactor = DisplayScale * UserScaleRatio; Refresh(); return DisplayScale;
    }
    /// <inheritdoc />
    public void Reset() => Reset(new Plot());
    /// <inheritdoc />
    public void Reset(Plot plot)
    {
        ArgumentNullException.ThrowIfNull(plot);
        if (!ReferenceEquals(Plot, plot)) Plot.Dispose();
        Plot = plot; Plot.PlotControl = this; Multiplot.Reset(plot); UserScaleRatio = 1;
        Plot.ScaleFactor = XamlRoot?.RasterizationScale ?? 1; Refresh();
    }
    /// <inheritdoc />
    public void ShowContextMenu(Pixel position) => Menu?.ShowContextMenu(position);
    /// <inheritdoc />
    public void SetCursor(Cursor cursor) => ProtectedCursor = Microsoft.UI.Input.InputSystemCursor.Create(cursor.GetCursor());
    /// <summary>Reads the current full GPU Figure once for an explicit export or diagnostic.</summary>
    /// <returns>A caller-owned bitmap containing GPU-rendered pixels.</returns>
    public SKBitmap CaptureBitmap() => GetRenderer().CaptureBitmap();
    private void OnPointerPressed(object sender, PointerRoutedEventArgs e) { Focus(FocusState.Pointer); UserInputProcessor.ProcessMouseDown(this, e); Panel.CapturePointer(e.Pointer); }
    private void OnPointerReleased(object sender, PointerRoutedEventArgs e) { UserInputProcessor.ProcessMouseUp(this, e); Panel.ReleasePointerCapture(e.Pointer); }
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e) => UserInputProcessor.ProcessMouseMove(this, e);
    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e) => UserInputProcessor.ProcessMouseWheel(this, e);
    private void OnPanelKeyDown(object sender, KeyRoutedEventArgs e) => UserInputProcessor.ProcessKeyDown(this, e);
    private void OnPanelKeyUp(object sender, KeyRoutedEventArgs e) => UserInputProcessor.ProcessKeyUp(this, e);
    private void OnCaptureLost(object sender, PointerRoutedEventArgs e) => UserInputProcessor.ProcessLostFocus();
    private void OnLostFocus(object sender, RoutedEventArgs e) => UserInputProcessor.ProcessLostFocus();
    private void ReleaseGpu()
    {
        try { Presenter?.Dispose(); } finally { Presenter = null; Renderer?.Dispose(); Renderer = null; }
    }
    /// <inheritdoc />
    public void Dispose()
    {
        if (Disposed) return;
        if (!DispatcherOwner.HasThreadAccess) throw new InvalidOperationException("Dispose the plot on its owning UI thread.");
        ReleaseGpu(); Disposed = true;
        if (ObservedRoot is not null) ObservedRoot.Changed -= OnRootChanged; ObservedRoot = null;
        Loaded -= OnLoaded; Unloaded -= OnUnloaded;
        if (Menu is IDisposable menu) menu.Dispose();
        GC.SuppressFinalize(this);
    }
}

