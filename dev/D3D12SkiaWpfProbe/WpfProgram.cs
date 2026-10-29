using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using ScottPlot.D3D12.Rendering;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;
using D11 = Vortice.Direct3D11;
using D9 = Vortice.Direct3D9;

/// <summary>Hosts an ordinary full Figure in a WPF D3DImage without pixel readback or software fallback.</summary>
internal sealed class WpfProgram : Window
{
    private readonly System.Windows.Controls.Image Host = new() { Stretch = Stretch.Fill };
    private readonly System.Windows.Controls.Grid Root = new();
    private readonly D3DImage Image = new();
    private readonly ScottPlot.Plot Plot = Program.CreatePlot();
    private readonly List<object> Events = new();
    private readonly string EvidenceDirectory;
    private readonly IDXGIFactory4 Factory;
    private readonly IDXGIAdapter1 Adapter;
    private readonly ID3D12Device Device;
    private readonly ID3D12CommandQueue Queue;
    private readonly ID3D12Fence Fence;
    private readonly ManualResetEvent WaitEvent = new(false);
    private readonly GRContext Context;
    private readonly D11.ID3D11Device Device11;
    private readonly D11.ID3D11DeviceContext Context11;
    private readonly D3D11On12Interop.On12Device On12;
    private readonly D9.IDirect3D9Ex D3D9;
    private readonly D9.IDirect3DDevice9Ex Device9;
    private ID3D12Resource? Target;
    private D11.ID3D11Resource? Wrapped;
    private D11.ID3D11Texture2D? Shared11;
    private D9.IDirect3DTexture9? Texture9;
    private D9.IDirect3DSurface9? Surface9;
    private GRBackendTexture? Texture;
    private SKSurface? Surface;
    private ulong FenceValue;
    private int PixelWidth;
    private int PixelHeight;
    private int Frames;
    private readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    [STAThread]
    private static void Main(string[] args)
    {
        string evidence = Path.GetFullPath(args[0]);
        try
        {
            Application app = new();
            app.Run(new WpfProgram(evidence));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(evidence, "wpf-failure.txt"), ex.ToString());
            Environment.ExitCode = 1;
        }
    }

    private WpfProgram(string evidence)
    {
        EvidenceDirectory = evidence;
        ShowActivated = false;
        Title = "ScottPlot full Figure - D3D12 GPU - WPF probe";
        Width = 1000; Height = 740;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Root.Children.Add(Host); Content = Root;
        Host.Source = Image;
        Factory = CreateDXGIFactory2<IDXGIFactory4>(false);
        Factory.EnumAdapters1(0, out IDXGIAdapter1 adapter).CheckError();
        Adapter = adapter;
        if ((Adapter.Description1.Flags & AdapterFlags.Software) != 0)
            throw new InvalidOperationException("Software adapter is forbidden.");
        Device = D3D12CreateDevice<ID3D12Device>(Adapter, FeatureLevel.Level_11_0);
        Queue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        Fence = Device.CreateFence(0);
        using GRD3DBackendContext backend = new() { Adapter = Adapter.NativePointer, Device = Device.NativePointer, Queue = Queue.NativePointer };
        Context = GRContext.CreateDirect3D(backend) ?? throw new InvalidOperationException("No Skia D3D12 context.");
        if (Context.Backend != GRBackend.Direct3D) throw new InvalidOperationException("Wrong Skia backend.");
        (Device11, Context11, On12) = D3D11On12Interop.Create(Device.NativePointer, Queue.NativePointer);
        D3D9 = D9.D3D9.Direct3DCreate9Ex();
        Device9 = D3D9.CreateDeviceEx(0, D9.DeviceType.Hardware, nint.Zero,
            D9.CreateFlags.HardwareVertexProcessing | D9.CreateFlags.Multithreaded | D9.CreateFlags.FpuPreserve,
            new D9.PresentParameters { Windowed = true, SwapEffect = D9.SwapEffect.Discard, DeviceWindowHandle = GetDesktopWindow() });
        Loaded += (_, _) => { RenderFrame("loaded"); Timer.Start(); };
        SizeChanged += (_, _) => { if (IsLoaded) RenderFrame("resize"); };
        Host.MouseWheel += (_, e) => { Plot.Axes.Zoom(e.Delta > 0 ? 1.2 : 1 / 1.2); RenderFrame("wheel"); };
        Timer.Tick += (_, _) => RenderFrame("stable");
        Closed += (_, _) => DisposeGpu();
    }

    private void RenderFrame(string reason)
    {
        try
        {
            if (!Image.IsFrontBufferAvailable) throw new InvalidOperationException("WPF D3DImage front buffer unavailable; no CPU fallback.");
            DpiScale dpi = VisualTreeHelper.GetDpi(this);
            int width = Math.Max(1, (int)Math.Ceiling(Root.ActualWidth * dpi.DpiScaleX));
            int height = Math.Max(1, (int)Math.Ceiling(Root.ActualHeight * dpi.DpiScaleY));
            if (width < 10 || height < 10) return;
            EnsureTargets(width, height);
            Plot.ScaleFactor = (float)dpi.DpiScaleX;
            // ScottPlot scales the supplied canvas and does not restore that transform.
            // A persistent GPU surface must preserve its original matrix and clip per frame.
            int saveCount = Surface!.Canvas.Save();
            try { Plot.Render(Surface); }
            finally { Surface.Canvas.RestoreToCount(saveCount); }
            Context.Flush(submit: true, synchronous: false);
            On12.AcquireWrappedResources(Wrapped!);
            Context11.CopyResource(Shared11!, Wrapped!);
            On12.ReleaseWrappedResources(Wrapped!);
            Context11.Flush();
            WaitForGpu();
            Image.Lock();
            try { Image.AddDirtyRect(new Int32Rect(0, 0, width, height)); }
            finally { Image.Unlock(); }
            Frames++;
            if (reason != "stable" || Frames == 3)
                Events.Add(new { reason, Frames, width, height, HostWidth = Host.ActualWidth, HostHeight = Host.ActualHeight, ImageWidth = Image.PixelWidth, ImageHeight = Image.PixelHeight, Dpi = dpi.DpiScaleX, Backend = Surface!.Context!.Backend.ToString() });
            if (reason != "stable" || Frames % 20 == 0) Save();
        }
        catch (Exception ex)
        {
            Timer.Stop();
            File.WriteAllText(Path.Combine(EvidenceDirectory, "wpf-failure.txt"), ex.ToString());
            Title = "FAILED - ScottPlot D3D12 WPF";
            throw;
        }
    }

    private void EnsureTargets(int width, int height)
    {
        if (Surface is not null && PixelWidth == width && PixelHeight == height) return;
        ReleaseTargets();
        PixelWidth = width; PixelHeight = height;
        Target = Device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
        using GRD3DTextureResourceInfo info = new() { Resource = Target.NativePointer, ResourceState = (uint)ResourceStates.RenderTarget,
            Format = (uint)Format.B8G8R8A8_UNorm, SampleCount = 1, LevelCount = 1 };
        Texture = new GRBackendTexture(width, height, info);
        Surface = SKSurface.Create(Context, Texture, GRSurfaceOrigin.TopLeft, 0, SKColorType.Bgra8888)
            ?? throw new InvalidOperationException("Cannot wrap D3D12 surface.");
        if (Surface.Context?.Backend != GRBackend.Direct3D || Surface.PeekPixels() is not null)
            throw new InvalidOperationException("GPU surface validation failed.");
        Wrapped = On12.CreateWrappedResource(Target, ResourceStates.RenderTarget, ResourceStates.RenderTarget);
        On12.ReleaseWrappedResources(Wrapped);
        Context11.Flush();
        Shared11 = Device11.CreateTexture2D(new D11.Texture2DDescription
        {
            Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0), Usage = D11.ResourceUsage.Default,
            BindFlags = D11.BindFlags.RenderTarget | D11.BindFlags.ShaderResource, MiscFlags = D11.ResourceOptionFlags.Shared,
        });
        using IDXGIResource resource = Shared11.QueryInterface<IDXGIResource>();
        nint handle = resource.SharedHandle;
        Texture9 = Device9.CreateTexture((uint)width, (uint)height, 1, D9.Usage.RenderTarget, D9.Format.A8R8G8B8, D9.Pool.Default, ref handle);
        Surface9 = Texture9.GetSurfaceLevel(0);
        Image.Lock();
        try { Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, Surface9.NativePointer, enableSoftwareFallback: false); }
        finally { Image.Unlock(); }
    }

    private void WaitForGpu()
    {
        Queue.Signal(Fence, ++FenceValue).CheckError();
        if (Fence.CompletedValue < FenceValue)
        {
            Fence.SetEventOnCompletion(FenceValue, WaitEvent.SafeWaitHandle.DangerousGetHandle()).CheckError();
            if (!WaitEvent.WaitOne(TimeSpan.FromSeconds(20))) throw new TimeoutException("GPU fence timeout.");
        }
        Device.DeviceRemovedReason.CheckError();
    }

    private void ReleaseTargets()
    {
        WaitForGpu();
        Image.Lock();
        try { Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, nint.Zero); }
        finally { Image.Unlock(); }
        Surface?.Dispose(); Surface = null;
        Texture?.Dispose(); Texture = null;
        Wrapped?.Dispose(); Wrapped = null;
        Surface9?.Dispose(); Surface9 = null;
        Texture9?.Dispose(); Texture9 = null;
        Shared11?.Dispose(); Shared11 = null;
        Target?.Dispose(); Target = null;
    }

    private void Save() => File.WriteAllText(Path.Combine(EvidenceDirectory, "wpf.json"),
        JsonSerializer.Serialize(new { Platform = "WPF", Backend = Context.Backend.ToString(), Adapter = Adapter.Description1.Description,
            Frames, PerFramePixelReadbacks = 0, SoftwareFallback = false, GpuCopyPerFrame = true, FenceWaitPerFrame = true, Events },
        new JsonSerializerOptions { WriteIndented = true }));

    private void DisposeGpu()
    {
        Timer.Stop(); ReleaseTargets(); Context.Dispose(); On12.Dispose(); Context11.Dispose(); Device11.Dispose();
        Device9.Dispose(); D3D9.Dispose(); Fence.Dispose(); WaitEvent.Dispose(); Queue.Dispose(); Device.Dispose();
        Adapter.Dispose(); Factory.Dispose(); Plot.Dispose(); SaveClosed();
    }

    private void SaveClosed() => File.WriteAllText(Path.Combine(EvidenceDirectory, "wpf-closed.txt"), "Window closed and resource Dispose path completed.");

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}






