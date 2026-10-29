using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

/// <summary>Reads the external D3D12 texture only for explicit handoff diagnostics, never during presentation.</summary>
internal static class ExternalTextureDiagnostic
{
    internal static unsafe object Save(ID3D12Device device, ID3D12CommandQueue queue, Action wait, ID3D12Resource source, int width, int height, string output)
    {
        int pitch = (width * 4 + 255) / 256 * 256;
        using ID3D12Resource readback = device.CreateCommittedResource(HeapType.Readback, HeapFlags.None,
            ResourceDescription.Buffer((ulong)pitch * (ulong)height), ResourceStates.CopyDest);
        using ID3D12CommandAllocator allocator = device.CreateCommandAllocator(CommandListType.Direct);
        using ID3D12GraphicsCommandList list = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, allocator);
        list.ResourceBarrier([ResourceBarrier.BarrierTransition(source, ResourceStates.RenderTarget, ResourceStates.CopySource)]);
        list.CopyTextureRegion(new TextureCopyLocation(readback, new PlacedSubresourceFootPrint
        {
            Offset = 0, Footprint = new SubresourceFootPrint { Format = Format.B8G8R8A8_UNorm, Width = (uint)width,
                Height = (uint)height, Depth = 1, RowPitch = (uint)pitch }
        }), 0, 0, 0, new TextureCopyLocation(source, 0), new Box(0, 0, 0, width, height, 1));
        list.ResourceBarrier([ResourceBarrier.BarrierTransition(source, ResourceStates.CopySource, ResourceStates.RenderTarget)]);
        list.Close(); queue.ExecuteCommandLists([list]); wait();
        using SKBitmap bitmap = new(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        byte* data = readback.Map<byte>(0);
        try
        {
            byte* destination = (byte*)bitmap.GetPixels();
            for (int row = 0; row < height; row++)
                Buffer.MemoryCopy(data + row * pitch, destination + row * bitmap.RowBytes, width * 4, width * 4);
        }
        finally { readback.Unmap(0); }
        using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        using Stream stream = File.Create(output); png.SaveTo(stream);
        byte[] pixels = bitmap.Bytes;
        int nonWhite = 0, colored = 0;
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] < 240 || pixels[i + 1] < 240 || pixels[i + 2] < 240) nonWhite++;
            if (Math.Max(pixels[i], Math.Max(pixels[i + 1], pixels[i + 2])) - Math.Min(pixels[i], Math.Min(pixels[i + 1], pixels[i + 2])) > 30) colored++;
        }
        return new { width, height, NonWhitePixels = nonWhite, ColoredPixels = colored, Readback = "One explicit D3D12 texture diagnostic; no Skia Snapshot/Encode occurs before copying texture." };
    }
}
