using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

/// <summary>Starts the real UWP Application with Windows.UI.Xaml projections, independently of WinUI 3.</summary>
internal static class UwpProgram
{
    internal static string Evidence => ApplicationData.Current.LocalFolder.Path;
    [MTAThread]
    private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(init => { _ = new UwpProbeApp(); });
    }
}

/// <summary>Owns the UWP CoreWindow and its native SwapChainPanel.</summary>
internal sealed class UwpProbeApp : Application
{
    private PanelRenderer? Renderer;

    internal UwpProbeApp()
    {
        UnhandledException += (_, e) => File.WriteAllText(Path.Combine(UwpProgram.Evidence, "uwp-unhandled.txt"), e.Exception.ToString());
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            SwapChainPanel panel = new();
            Grid root = new();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            StackPanel commands = new() { Orientation = Orientation.Horizontal };
            Button zoom = new() { Content = "Zoom probe" };
            Button reset = new() { Content = "Reset probe" };
            commands.Children.Add(zoom); commands.Children.Add(reset);
            root.Children.Add(commands); Grid.SetRow(panel, 1); root.Children.Add(panel);
            Window.Current.Content = root;
            Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().Title = "ScottPlot full Figure - D3D12 GPU - UWP probe";
            Windows.UI.ViewManagement.ApplicationView.PreferredLaunchViewSize = new Windows.Foundation.Size(900, 680);
            Windows.UI.ViewManagement.ApplicationView.PreferredLaunchWindowingMode = Windows.UI.ViewManagement.ApplicationViewWindowingMode.PreferredLaunchViewSize;
            panel.Loaded += (_, _) => { Renderer = new PanelRenderer(panel); Renderer.Start(); };
            zoom.Click += (_, _) => Renderer?.Zoom();
            reset.Click += (_, _) => Renderer?.Reset();
            Window.Current.Closed += (_, _) => Renderer?.Dispose();
            Window.Current.Activate();
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(UwpProgram.Evidence, "uwp-failure.txt"), ex.ToString());
            throw;
        }
    }
}

/// <summary>Draws the normal Figure to a Skia D3D12 texture and copies it entirely on the GPU to DXGI back buffers.</summary>
internal sealed class PanelRenderer : IDisposable
{
    private readonly SwapChainPanel Panel;
    private readonly ScottPlot.Plot Plot = Program.CreatePlot();
    private readonly IDXGIFactory4 Factory;
    private readonly IDXGIAdapter1 Adapter;
    private readonly ID3D12Device Device;
    private readonly ID3D12CommandQueue Queue;
    private readonly ID3D12CommandAllocator Allocator;
    private readonly ID3D12GraphicsCommandList List;
    private readonly ID3D12Fence Fence;
    private readonly AutoResetEvent WaitEvent = new(false);
    private readonly GRContext Context;
    private readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly List<object> Events = new();
    private IDXGISwapChain3? SwapChain;
    private ID3D12Resource? Target;
    private GRBackendTexture? Texture;
    private SKSurface? Surface;
    private ulong FenceValue;
    private int Width, Height, Frames;
    private nint NativePanel;

    internal PanelRenderer(SwapChainPanel panel)
    {
        Panel = panel;
        Factory = CreateDXGIFactory2<IDXGIFactory4>(false);
        Factory.EnumAdapters1(0, out IDXGIAdapter1 adapter).CheckError();
        Adapter = adapter;
        if ((Adapter.Description1.Flags & AdapterFlags.Software) != 0) throw new InvalidOperationException("Software adapter forbidden.");
        Device = D3D12CreateDevice<ID3D12Device>(Adapter, FeatureLevel.Level_11_0);
        Queue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        Allocator = Device.CreateCommandAllocator(CommandListType.Direct);
        List = Device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, Allocator);
        List.Close();
        Fence = Device.CreateFence(0);
        using GRD3DBackendContext backend = new() { Adapter = Adapter.NativePointer, Device = Device.NativePointer, Queue = Queue.NativePointer };
        Context = GRContext.CreateDirect3D(backend) ?? throw new InvalidOperationException("No D3D12 Skia context.");
        if (Context.Backend != GRBackend.Direct3D) throw new InvalidOperationException("Wrong Skia backend.");
        Guid iid = new("F92F19D2-3ADE-45A6-A20C-F6F1EA90554B");
        nint inspectable = WinRT.MarshalInspectable<SwapChainPanel>.FromManaged(panel);
        try { Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, in iid, out NativePanel)); }
        finally { Marshal.Release(inspectable); }
        Panel.CompositionScaleChanged += (_, _) => Render("composition-scale");
        Panel.SizeChanged += (_, _) => Render("resize");
        Panel.PointerWheelChanged += (_, e) => { if (e.GetCurrentPoint(Panel).Properties.MouseWheelDelta > 0) Zoom(); else { Plot.Axes.Zoom(1 / 1.2); Render("wheel-out"); } };
        Timer.Tick += (_, _) => Render("stable");
    }

    internal void Start() { Render("first"); Timer.Start(); }
    internal void Zoom() { Plot.Axes.Zoom(1.2); Render("zoom-invoke"); }
    internal void Reset() { Plot.Axes.SetLimits(-1, 11, -1.5, 1.8); Render("reset-invoke"); }

    private void Render(string reason)
    {
        try
        {
            double scale = Panel.XamlRoot.RasterizationScale;
            int width = Math.Max(1, (int)Math.Ceiling(Panel.ActualWidth * Panel.CompositionScaleX));
            int height = Math.Max(1, (int)Math.Ceiling(Panel.ActualHeight * Panel.CompositionScaleY));
            if (width < 10 || height < 10) return;
            EnsureTargets(width, height);
            Plot.ScaleFactor = scale;
            int save = Surface!.Canvas.Save();
            try { Plot.Render(Surface); }
            finally { Surface.Canvas.RestoreToCount(save); }
            if (Surface.Context?.Backend != GRBackend.Direct3D || Context.IsAbandoned)
                throw new InvalidOperationException("Lost GPU surface/context.");
            Context.Flush(true, false);
            using ID3D12Resource backBuffer = SwapChain!.GetBuffer<ID3D12Resource>(SwapChain.CurrentBackBufferIndex);
            Allocator.Reset(); List.Reset(Allocator);
            Transition(Target!, ResourceStates.RenderTarget, ResourceStates.CopySource);
            Transition(backBuffer, ResourceStates.Present, ResourceStates.CopyDest);
            List.CopyResource(backBuffer, Target!);
            Transition(Target!, ResourceStates.CopySource, ResourceStates.RenderTarget);
            Transition(backBuffer, ResourceStates.CopyDest, ResourceStates.Present);
            List.Close(); Queue.ExecuteCommandLists([List]);
            SwapChain.Present(1, PresentFlags.None).CheckError();
            Wait();
            Frames++;
            if (reason != "stable" || Frames == 3)
                Events.Add(new { reason, Frames, width, height, Dpi = scale, LogicalWidth = Panel.ActualWidth, LogicalHeight = Panel.ActualHeight, Panel.CompositionScaleX, Panel.CompositionScaleY });
            if (reason != "stable" || Frames % 20 == 0) Save();
        }
        catch (Exception ex)
        {
            Timer.Stop();
            File.WriteAllText(Path.Combine(UwpProgram.Evidence, "uwp-failure.txt"), ex.ToString());
            throw;
        }
    }

    private void EnsureTargets(int width, int height)
    {
        if (Surface is not null && Width == width && Height == height) return;
        ReleaseTargets(); Width = width; Height = height;
        using IDXGISwapChain1 swap = Factory.CreateSwapChainForComposition(Queue, new SwapChainDescription1
        {
            Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm, BufferCount = 2,
            BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
            Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied,
        });
        SwapChain = swap.QueryInterface<IDXGISwapChain3>();
        SwapChain.MatrixTransform = System.Numerics.Matrix3x2.CreateScale(1 / Panel.CompositionScaleX, 1 / Panel.CompositionScaleY);
        SetSwapChain(SwapChain.NativePointer);
        Target = Device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
        using GRD3DTextureResourceInfo info = new() { Resource = Target.NativePointer, ResourceState = (uint)ResourceStates.RenderTarget,
            Format = (uint)Format.B8G8R8A8_UNorm, SampleCount = 1, LevelCount = 1 };
        Texture = new GRBackendTexture(width, height, info);
        Surface = SKSurface.Create(Context, Texture, GRSurfaceOrigin.TopLeft, 0, SKColorType.Bgra8888)
            ?? throw new InvalidOperationException("Cannot wrap D3D12 texture.");
        if (Surface.Context?.Backend != GRBackend.Direct3D || Surface.PeekPixels() is not null)
            throw new InvalidOperationException("Not a GPU surface.");
    }

    private unsafe void SetSwapChain(nint chain)
    {
        nint* vtable = *(nint**)NativePanel;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[3])(NativePanel, chain);
        Marshal.ThrowExceptionForHR(hr);
    }

    private void Transition(ID3D12Resource resource, ResourceStates from, ResourceStates to) =>
        List.ResourceBarrier([ResourceBarrier.BarrierTransition(resource, from, to)]);

    private void Wait()
    {
        Queue.Signal(Fence, ++FenceValue).CheckError();
        if (Fence.CompletedValue < FenceValue)
        {
            Fence.SetEventOnCompletion(FenceValue, WaitEvent.SafeWaitHandle.DangerousGetHandle()).CheckError();
            if (!WaitEvent.WaitOne(TimeSpan.FromSeconds(20))) throw new TimeoutException("GPU fence timed out.");
        }
        Device.DeviceRemovedReason.CheckError();
    }

    private void Save() => File.WriteAllText(Path.Combine(UwpProgram.Evidence, "uwp.json"),
        JsonSerializer.Serialize(new { Platform = "UWP .NET 10 / Windows.UI.Xaml", Backend = Context.Backend.ToString(), Adapter = Adapter.Description1.Description,
            Frames, PixelReadbacks = 0, CpuFallbacks = 0, GpuCopyPerFrame = true, FenceWaitPerFrame = true, Events }, new JsonSerializerOptions { WriteIndented = true }));

    private void ReleaseTargets()
    {
        Wait(); SetSwapChain(nint.Zero);
        Surface?.Dispose(); Surface = null; Texture?.Dispose(); Texture = null;
        Target?.Dispose(); Target = null; SwapChain?.Dispose(); SwapChain = null;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Timer.Stop(); ReleaseTargets(); Marshal.Release(NativePanel);
        Context.Dispose(); List.Dispose(); Allocator.Dispose(); Fence.Dispose(); WaitEvent.Dispose();
        Queue.Dispose(); Device.Dispose(); Adapter.Dispose(); Factory.Dispose(); Plot.Dispose();
        File.WriteAllText(Path.Combine(UwpProgram.Evidence, "uwp-closed.txt"), "Window closed; Dispose path completed.");
    }
}






