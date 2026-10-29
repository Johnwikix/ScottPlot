using System.Diagnostics;
using ScottPlot;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

/// <summary>Owns a hardware-only D3D12 GPU context and explicitly waits without reading pixels.</summary>
internal sealed class GpuHarness : IDisposable
{
    internal readonly IDXGIFactory4 Factory = CreateDXGIFactory2<IDXGIFactory4>(false);
    internal readonly IDXGIAdapter1 Adapter;
    internal readonly IDXGIAdapter3 MemoryAdapter;
    internal readonly ID3D12Device Device;
    internal readonly ID3D12CommandQueue Queue;
    private readonly ID3D12Fence Fence;
    private readonly AutoResetEvent Event = new(false);
    private readonly GRD3DBackendContext Backend;
    internal readonly GRContext Context;
    private ulong Sequence;

    internal GpuHarness()
    {
        Factory.EnumAdapters1(0, out Adapter).CheckError();
        if ((Adapter.Description1.Flags & AdapterFlags.Software) != 0) throw new InvalidOperationException("Software adapter rejected.");
        MemoryAdapter = Adapter.QueryInterface<IDXGIAdapter3>();
        Device = D3D12CreateDevice<ID3D12Device>(Adapter, FeatureLevel.Level_11_0);
        Queue = Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
        Fence = Device.CreateFence(0);
        Backend = new() { Adapter = Adapter.NativePointer, Device = Device.NativePointer, Queue = Queue.NativePointer };
        Context = GRContext.CreateDirect3D(Backend) ?? throw new InvalidOperationException("No D3D12 context; no fallback.");
        if (Context.Backend != GRBackend.Direct3D || Context.GetMaxSurfaceSampleCount(SKColorType.Bgra8888) < 4)
            throw new InvalidOperationException("Hardware D3D12 with 4x MSAA required.");
        Context.SetResourceCacheLimit(64L * 1024 * 1024);
    }

    internal void Wait()
    {
        Queue.Signal(Fence, ++Sequence).CheckError();
        if (Fence.CompletedValue < Sequence)
        {
            Fence.SetEventOnCompletion(Sequence, Event.SafeWaitHandle.DangerousGetHandle()).CheckError();
            if (!Event.WaitOne(TimeSpan.FromSeconds(20))) throw new TimeoutException("GPU fence timeout.");
        }
        Device.DeviceRemovedReason.CheckError();
        if (Context.IsAbandoned) throw new InvalidOperationException("GPU context abandoned.");
    }

    internal object Memory(string stage)
    {
        using Process process = Process.GetCurrentProcess();
        Context.GetResourceCacheUsage(out int resources, out long cacheBytes);
        var local = MemoryAdapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
        var nonlocal = MemoryAdapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal);
        return new { Stage = stage, process.PrivateMemorySize64, process.WorkingSet64, process.HandleCount,
            ManagedLiveEstimateBytes = GC.GetTotalMemory(false), ManagedCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
            Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2),
            SkiaCacheBytes = cacheBytes, SkiaCacheResources = resources, LocalGpuUsageBytes = local.CurrentUsage,
            LocalGpuBudgetBytes = local.Budget, NonLocalGpuUsageBytes = nonlocal.CurrentUsage };
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Context.Flush(true, false); Wait(); Context.Dispose(); Backend.Dispose(); Event.Dispose();
        Fence.Dispose(); Queue.Dispose(); Device.Dispose(); MemoryAdapter.Dispose(); Adapter.Dispose(); Factory.Dispose();
    }
}

/// <summary>Wraps a native texture with a four-sample Skia GPU surface.</summary>
internal sealed class GpuTarget : IDisposable
{
    internal readonly ID3D12Resource Resource;
    private readonly GRD3DTextureResourceInfo Info;
    private readonly GRBackendTexture Texture;
    internal readonly SKSurface Surface;
    internal GpuTarget(GpuHarness gpu, int width = 800, int height = 600)
    {
        Resource = gpu.Device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
        Info = new() { Resource = Resource.NativePointer, ResourceState = (uint)ResourceStates.RenderTarget,
            Format = (uint)Format.B8G8R8A8_UNorm, SampleCount = 1, LevelCount = 1 };
        Texture = new(width, height, Info);
        Surface = SKSurface.Create(gpu.Context, Texture, GRSurfaceOrigin.TopLeft, 4, SKColorType.Bgra8888)
            ?? throw new InvalidOperationException("No GPU MSAA surface; no fallback.");
        if (Surface.Context?.Backend != GRBackend.Direct3D || Surface.PeekPixels() is not null)
            throw new InvalidOperationException("Raster surface rejected.");
    }

    internal void Render(Plot plot)
    {
        int saved = Surface.Canvas.Save();
        try { plot.Render(Surface); }
        finally { Surface.Canvas.RestoreToCount(saved); }
    }

    internal byte[] DiagnosticPixels(string output)
    {
        using SKImage image = Surface.Snapshot();
        if (!image.IsTextureBacked) throw new InvalidOperationException("GPU snapshot required.");
        using SKBitmap bitmap = new(new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        if (!image.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0)) throw new InvalidOperationException("Diagnostic read failed.");
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using Stream stream = File.Create(output);
        png.SaveTo(stream);
        return bitmap.Bytes;
    }

    /// <inheritdoc />
    public void Dispose() { Surface.Dispose(); Texture.Dispose(); Info.Dispose(); Resource.Dispose(); }
}


