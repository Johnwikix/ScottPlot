using ScottPlot;
using SkiaSharp;
using Vortice.Direct3D12;
using Vortice.DXGI;

/// <summary>Owns an explicit native MSAA render target and a single-sample texture for external GPU presentation.</summary>
/// <remarks>The caller must finish the previous frame's fence before reuse or release. Every Figure is redrawn inside its actual viewport.</remarks>
internal sealed class GpuFigureSurface : IDisposable
{
    private readonly ID3D12Device Device;
    private readonly GRContext Context;
    private readonly ID3D12CommandQueue Queue;
    private readonly bool ExactSize;
    private readonly ID3D12CommandAllocator Allocator;
    private readonly ID3D12GraphicsCommandList List;
    internal ID3D12Resource? Resource { get; private set; }
    private ID3D12Resource? Multisample;
    internal SKSurface? Surface { get; private set; }
    private GRBackendRenderTarget? RenderTarget;
    internal int Width { get; private set; }
    internal int Height { get; private set; }
    internal int Allocations { get; private set; }
    internal int ResolveSubmissions { get; private set; }

    internal GpuFigureSurface(ID3D12Device device, GRContext context, ID3D12CommandQueue queue, bool exactSize = false)
    {
        Device = device; Context = context; Queue = queue; ExactSize = exactSize;
        Allocator = Device.CreateCommandAllocator(CommandListType.Direct);
        List = Device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, Allocator);
        List.Close();
    }

    internal bool Fits(int width, int height) => Surface is not null &&
        (ExactSize ? Width == width && Height == height : Width >= width && Height >= height);

    internal void Ensure(int width, int height)
    {
        if (Fits(width, height)) return;
        if (Context.Backend != GRBackend.Direct3D || Context.GetMaxSurfaceSampleCount(SKColorType.Bgra8888) < 4)
            throw new InvalidOperationException("Hardware Direct3D surface with native MSAA4 required; no fallback.");
        int capacityWidth = ExactSize ? width : Grow(Width, width), capacityHeight = ExactSize ? height : Grow(Height, height);
        Release();
        Context.PurgeUnlockedResources(false);
        Resource = Device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)capacityWidth, (uint)capacityHeight, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
        Multisample = Device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
            ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)capacityWidth, (uint)capacityHeight, 1, 1, 4, 0, ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
        using GRD3DTextureResourceInfo info = new() { Resource = Multisample.NativePointer, ResourceState = (uint)ResourceStates.RenderTarget,
            Format = (uint)Format.B8G8R8A8_UNorm, SampleCount = 4, LevelCount = 1 };
        RenderTarget = new(capacityWidth, capacityHeight, info);
        if (!RenderTarget.IsValid || RenderTarget.SampleCount != 4) throw new InvalidOperationException("Native MSAA render-target wrapper invalid.");
        Surface = SKSurface.Create(Context, RenderTarget, GRSurfaceOrigin.TopLeft, SKColorType.Bgra8888)
            ?? throw new InvalidOperationException("D3D12 MSAA render-target surface creation failed; no fallback.");
        if (Surface.Context?.Backend != GRBackend.Direct3D || Surface.PeekPixels() is not null)
            throw new InvalidOperationException("GPU-backed surface required.");
        Width = capacityWidth; Height = capacityHeight; Allocations++;
    }

    internal void Render(Plot plot, int width, int height)
    {
        if (!Fits(width, height)) throw new InvalidOperationException("Viewport exceeds GPU capacity.");
        SKCanvas canvas = Surface!.Canvas;
        int saved = canvas.Save();
        try { canvas.ClipRect(new SKRect(0, 0, width, height)); plot.Render(Surface); }
        finally { canvas.RestoreToCount(saved); }
    }

    internal void SubmitForExternalUse()
    {
        Context.Flush(true, false);
        Allocator.Reset(); List.Reset(Allocator);
        List.ResourceBarrier([
            ResourceBarrier.BarrierTransition(Multisample!, ResourceStates.RenderTarget, ResourceStates.ResolveSource),
            ResourceBarrier.BarrierTransition(Resource!, ResourceStates.RenderTarget, ResourceStates.ResolveDest)]);
        List.ResolveSubresource(Resource!, 0, Multisample!, 0, Format.B8G8R8A8_UNorm);
        List.ResourceBarrier([
            ResourceBarrier.BarrierTransition(Multisample!, ResourceStates.ResolveSource, ResourceStates.RenderTarget),
            ResourceBarrier.BarrierTransition(Resource!, ResourceStates.ResolveDest, ResourceStates.RenderTarget)]);
        List.Close(); Queue.ExecuteCommandLists([List]); ResolveSubmissions++;
    }

    internal void Release()
    {
        Surface?.Dispose(); Surface = null;
        RenderTarget?.Dispose(); RenderTarget = null;
        Multisample?.Dispose(); Multisample = null;
        Resource?.Dispose(); Resource = null;
        Width = Height = 0;
    }
    private static int Grow(int current, int required)
    {
        if (required < 1 || required > 16384) throw new ArgumentOutOfRangeException(nameof(required));
        if (current >= required) return current;
        int proposed = Math.Max(required, (int)Math.Ceiling(current * 1.5));
        return Math.Min(16384, checked((proposed + 127) / 128 * 128));
    }
    /// <inheritdoc />
    public void Dispose() { Release(); List.Dispose(); Allocator.Dispose(); }
}
