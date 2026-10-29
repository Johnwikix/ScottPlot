using ScottPlot.D3D12.Rendering.Internal;
using SkiaSharp;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace ScottPlot.D3D12.Rendering;

public sealed partial class D3D12Renderer
{
    /// <summary>Gets the number of explicit captures requested by the caller; normal rendering never increments this.</summary>
    public long PixelReadbackCount { get; private set; }

    /// <summary>Captures the most recent resolved GPU Figure for explicit export or diagnostics.</summary>
    /// <returns>A caller-owned CPU bitmap containing GPU-rendered pixels.</returns>
    /// <remarks>This performs one GPU-to-CPU transfer. It is never invoked by the render or presentation loops.</remarks>
    public unsafe SKBitmap CaptureBitmap()
    {
        ObjectDisposedException.ThrowIf(Disposed, this); Device.VerifyAccess();
        int width = LastFrame.Width, height = LastFrame.Height;
        if (width == 0 || height == 0) throw new InvalidOperationException("Render a Figure before capturing it.");
        Device.WaitForIdle();
        int pitch = (width * 4 + 255) / 256 * 256;
        using ID3D12Resource readback = Device.NativeDevice.CreateCommittedResource(HeapType.Readback, HeapFlags.None,
            ResourceDescription.Buffer((ulong)pitch * (ulong)height), ResourceStates.CopyDest);
        using ID3D12CommandAllocator allocator = Device.NativeDevice.CreateCommandAllocator(CommandListType.Direct);
        using ID3D12GraphicsCommandList list = Device.NativeDevice.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator);
        GpuCommands.Transition(list, ResolvedTexture, ResourceStates.RenderTarget, ResourceStates.CopySource);
        list.CopyTextureRegion(new TextureCopyLocation(readback, new PlacedSubresourceFootPrint
        {
            Offset = 0, Footprint = new SubresourceFootPrint { Format = Format.B8G8R8A8_UNorm, Width = (uint)width, Height = (uint)height, Depth = 1, RowPitch = (uint)pitch }
        }), 0, 0, 0, new TextureCopyLocation(ResolvedTexture, 0), new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));
        GpuCommands.Transition(list, ResolvedTexture, ResourceStates.CopySource, ResourceStates.RenderTarget);
        list.Close(); GpuCommands.Execute(Device.CommandQueue, list); Device.WaitForIdle();
        SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        try
        {
            byte* source = readback.Map<byte>(0);
            try
            {
                byte* destination = (byte*)bitmap.GetPixels();
                for (int row = 0; row < height; row++) Buffer.MemoryCopy(source + (long)row * pitch, destination + (long)row * bitmap.RowBytes, width * 4, width * 4);
            }
            finally { readback.Unmap(0); }
            PixelReadbackCount++; return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }
}
