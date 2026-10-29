using ScottPlot.Plottables;
using SharpGen.Runtime;
using SkiaSharp;
using System.Numerics;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScottPlot.D3D12.Rendering;

internal sealed unsafe class D3D12CurveRenderer : IDisposable
{
    private const int VertexStride = 24;

    private D3D12SharedDevice? _shared;
    private ID3D12CommandQueue? _queue;
    private ID3D12CommandAllocator? _allocator;
    private ID3D12GraphicsCommandList? _commandList;
    private ID3D12Fence? _fence;
    private ManualResetEvent? _fenceEvent;
    private ulong _fenceValue;

    private ID3D12Resource? _renderTarget;
    private ID3D12Resource? _resolveTarget;
    private ID3D12DescriptorHeap? _rtvHeap;
    private CpuDescriptorHandle _rtvHandle;
    private ID3D12Resource? _readback;
    private ID3D12Resource? _vertexUpload;
    private nint _uploadCpu;
    private int _uploadBytes;
    private int _readbackRowPitch;
    private SKBitmap? _bitmap;
    private int _width;
    private int _height;
    private bool _antiAlias;
    private readonly float[] _viewConstants = new float[4];
    private bool _isBroken;

    private readonly struct CurvePixels(D3D12SignalXY signal, IReadOnlyList<Pixel> pixels)
    {
        public D3D12SignalXY Signal { get; } = signal;
        public IReadOnlyList<Pixel> Pixels { get; } = pixels;
    }

    public D3D12CurveRenderer()
    {
        try
        {
            _shared = D3D12SharedDevice.Acquire();
            if (_shared is null)
            {
                _isBroken = true;
                return;
            }

            ID3D12Device device = _shared.Device;
            _queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct, 0, CommandQueueFlags.None, 0));
            _allocator = device.CreateCommandAllocator(CommandListType.Direct);
            _commandList = device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, _allocator, _shared.PipelineState);
            _commandList.Close();
            _fence = device.CreateFence(0, FenceFlags.None);
            _fenceEvent = new ManualResetEvent(false);
        }
        catch
        {
            _isBroken = true;
            Dispose();
        }
    }

    public bool Render(RenderPack rp, IReadOnlyList<D3D12SignalXY> signals)
    {
        if (_isBroken || _shared is null)
            return false;

        try
        {
            return RenderCore(rp, signals);
        }
        catch
        {
            _isBroken = true;
            Dispose();
            return false;
        }
    }

    private bool RenderCore(RenderPack rp, IReadOnlyList<D3D12SignalXY> signals)
    {
        if (!TryGetScale(rp.Canvas.TotalMatrix, out float scaleX, out float scaleY))
            return false;

        int width = (int)Math.Ceiling(rp.DataRect.Width * scaleX);
        int height = (int)Math.Ceiling(rp.DataRect.Height * scaleY);
        if (width < 1 || height < 1 || width > 16384 || height > 16384 || (long)width * height > 100_000_000)
            return false;

        List<CurvePixels> curves = new(signals.Count);
        long vertexCapacity = 0;
        foreach (D3D12SignalXY signal in signals)
        {
            IReadOnlyList<Pixel> pixels = signal.Data.GetPixelsToDraw(rp, signal.Axes, ConnectStyle.Straight);
            if (pixels.Count < 2)
                continue;

            curves.Add(new CurvePixels(signal, pixels));
            vertexCapacity += (long)(pixels.Count - 1) * 12;
        }

        if (curves.Count == 0)
            return true;

        long uploadLength = vertexCapacity * VertexStride;
        if (uploadLength <= 0 || uploadLength > int.MaxValue - 65535L)
            return false;

        bool antiAlias = signals[0].LineStyle.AntiAlias;
        EnsureTargets(width, height, antiAlias);
        EnsureUpload((int)uploadLength);

        int[] starts = new int[curves.Count];
        int[] counts = new int[curves.Count];
        int totalVertices = 0;
        for (int i = 0; i < curves.Count; i++)
        {
            starts[i] = totalVertices;
            totalVertices += WriteCurve(curves[i].Signal, curves[i].Pixels, scaleX, scaleY, -rp.DataRect.Left * scaleX, -rp.DataRect.Top * scaleY, _uploadCpu + totalVertices * VertexStride);
            counts[i] = totalVertices - starts[i];
        }

        if (totalVertices == 0)
            return true;

        ID3D12GraphicsCommandList list = _commandList!;
        ID3D12PipelineState pipelineState = antiAlias ? _shared!.PipelineStateMsaa : _shared!.PipelineState;
        _allocator!.Reset();
        list.Reset(_allocator!, pipelineState);
        list.RSSetViewports(new Viewport(0, 0, width, height));
        list.RSSetScissorRects([new global::Vortice.RawRect(0, 0, width, height)]);
        list.SetGraphicsRootSignature(_shared.RootSignature);
        list.OMSetRenderTargets(_rtvHandle, null);
        list.ClearRenderTargetView(_rtvHandle, new Color4(0, 0, 0, 0));
        _viewConstants[0] = width;
        _viewConstants[1] = height;
        list.SetGraphicsRoot32BitConstants(0, _viewConstants, 0);
        list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        list.IASetVertexBuffers(0, new VertexBufferView(_vertexUpload!.GPUVirtualAddress, (uint)(totalVertices * VertexStride), VertexStride));

        for (int i = 0; i < curves.Count; i++)
        {
            if (counts[i] > 0)
                list.DrawInstanced((uint)counts[i], 1, (uint)starts[i], 0);
        }

        ID3D12Resource copySource = _renderTarget!;
        ResourceStates restoreState = ResourceStates.RenderTarget;
        if (antiAlias)
        {
            ResourceBarrier toResolveSource = ResourceBarrier.BarrierTransition(_renderTarget!, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
            list.ResourceBarrier([toResolveSource]);
            list.ResolveSubresource(_resolveTarget!, 0, _renderTarget!, 0, Format.B8G8R8A8_UNorm);
            copySource = _resolveTarget!;
            restoreState = ResourceStates.ResolveDest;
        }

        ResourceBarrier toCopy = ResourceBarrier.BarrierTransition(copySource, restoreState, ResourceStates.CopySource);
        list.ResourceBarrier([toCopy]);
        list.CopyTextureRegion(
            new TextureCopyLocation(_readback!, new PlacedSubresourceFootPrint
            {
                Offset = 0,
                Footprint = new SubresourceFootPrint
                {
                    Format = Format.B8G8R8A8_UNorm,
                    Width = (uint)width,
                    Height = (uint)height,
                    Depth = 1,
                    RowPitch = (uint)_readbackRowPitch,
                },
            }),
            0, 0, 0,
            new TextureCopyLocation(copySource, 0),
            null);
        ResourceBarrier restoreCopySource = ResourceBarrier.BarrierTransition(copySource, ResourceStates.CopySource, restoreState);
        list.ResourceBarrier([restoreCopySource]);
        if (antiAlias)
        {
            ResourceBarrier restoreRenderTarget = ResourceBarrier.BarrierTransition(_renderTarget!, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
            list.ResourceBarrier([restoreRenderTarget]);
        }
        list.Close();
        _queue!.ExecuteCommandLists([list]);
        _queue.Signal(_fence!, ++_fenceValue).CheckError();
        WaitForFence(_fenceValue);
        CopyToBitmap();

        using SKImage image = SKImage.FromPixels(_bitmap!.Info, _bitmap.GetPixels(), _bitmap.RowBytes);
        using SKPaint paint = new()
        {
            BlendMode = SKBlendMode.SrcOver,
            IsAntialias = false,
        };
        SKSamplingOptions sampling = new(SKFilterMode.Nearest, SKMipmapMode.None);
        rp.Canvas.DrawImage(image, rp.DataRect.ToSKRect(), sampling, paint);
        return true;
    }

    internal static int WriteCurve(D3D12SignalXY signal, IReadOnlyList<Pixel> pixels, float scaleX, float scaleY, float offsetX, float offsetY, nint destination)
    {
        Color color = signal.LineColor;
        float red = color.Red / 255f;
        float green = color.Green / 255f;
        float blue = color.Blue / 255f;
        float scale = (scaleX + scaleY) / 2;
        float width = signal.LineStyle.Hairline ? 1 : signal.LineWidth * scale;
        float half = width / 2;
        float miterLimit = signal.LineStyle.StrokeMiter;
        float* output = (float*)destination;
        int vertex = 0;
        bool hasAnchor = false;
        bool hasSegment = false;
        Vector2 anchor = default;
        Vector2 segmentStart = default;
        Vector2 segmentEnd = default;
        Vector2 normal = default;
        Vector2 startOffset = default;

        void EmitTriangle(Vector2 a, Vector2 b, Vector2 c)
        {
            WriteVertex(output, vertex++, a, red, green, blue);
            WriteVertex(output, vertex++, b, red, green, blue);
            WriteVertex(output, vertex++, c, red, green, blue);
        }

        void EmitQuad(Vector2 start, Vector2 end, Vector2 fromOffset, Vector2 toOffset)
        {
            Vector2 a = start - fromOffset;
            Vector2 b = start + fromOffset;
            Vector2 c = end - toOffset;
            Vector2 d = end + toOffset;
            EmitTriangle(a, c, b);
            EmitTriangle(b, c, d);
        }

        for (int i = 0; i < pixels.Count; i++)
        {
            Pixel pixel = pixels[i];
            if (!IsFinite(pixel))
            {
                if (hasSegment)
                    EmitQuad(segmentStart, segmentEnd, startOffset, normal * half);
                hasAnchor = false;
                hasSegment = false;
                continue;
            }

            Vector2 point = new(pixel.X * scaleX + offsetX, pixel.Y * scaleY + offsetY);
            if (!hasSegment)
            {
                if (!hasAnchor)
                {
                    anchor = point;
                    hasAnchor = true;
                    continue;
                }

                Vector2 initialDelta = point - anchor;
                float initialLengthSquared = initialDelta.LengthSquared();
                if (initialLengthSquared < 1e-12f)
                    continue;

                segmentStart = anchor;
                segmentEnd = point;
                Vector2 initialDirection = initialDelta / MathF.Sqrt(initialLengthSquared);
                normal = new(-initialDirection.Y, initialDirection.X);
                startOffset = normal * half;
                hasSegment = true;
                continue;
            }

            Vector2 delta = point - segmentEnd;
            float lengthSquared = delta.LengthSquared();
            if (lengthSquared < 1e-12f)
                continue;

            Vector2 nextDirection = delta / MathF.Sqrt(lengthSquared);
            Vector2 nextNormal = new(-nextDirection.Y, nextDirection.X);
            Vector2 miter = normal + nextNormal;
            float miterLengthSquared = miter.LengthSquared();
            float dot = miterLengthSquared > 0 ? Vector2.Dot(Vector2.Normalize(miter), normal) : 0;
            float ratio = dot > 0 ? 1 / dot : float.PositiveInfinity;
            bool bevel = !float.IsFinite(ratio) || ratio > miterLimit;
            Vector2 endOffset;
            Vector2 nextStartOffset;
            if (bevel)
            {
                endOffset = normal * half;
                nextStartOffset = nextNormal * half;
            }
            else
            {
                endOffset = Vector2.Normalize(miter) * (half * ratio);
                nextStartOffset = endOffset;
            }

            EmitQuad(segmentStart, segmentEnd, startOffset, endOffset);
            if (bevel)
            {
                EmitTriangle(segmentEnd, segmentEnd + normal * half, segmentEnd + nextNormal * half);
                EmitTriangle(segmentEnd, segmentEnd - normal * half, segmentEnd - nextNormal * half);
            }

            segmentStart = segmentEnd;
            segmentEnd = point;
            normal = nextNormal;
            startOffset = nextStartOffset;
        }

        if (hasSegment)
            EmitQuad(segmentStart, segmentEnd, startOffset, normal * half);

        return vertex;
    }

    private static void WriteVertex(float* output, int vertex, Vector2 position, float r, float g, float b)
    {
        float* target = output + vertex * 6;
        target[0] = position.X;
        target[1] = position.Y;
        target[2] = r;
        target[3] = g;
        target[4] = b;
        target[5] = 1;
    }

    private static bool IsFinite(Pixel pixel) => float.IsFinite(pixel.X) && float.IsFinite(pixel.Y);

    private static bool TryGetScale(SKMatrix matrix, out float scaleX, out float scaleY)
    {
        scaleX = matrix.ScaleX;
        scaleY = matrix.ScaleY;
        if (!float.IsFinite(scaleX) || !float.IsFinite(scaleY) || scaleX <= 0 || scaleY <= 0)
            return false;
        if (!float.IsFinite(matrix.TransX) || !float.IsFinite(matrix.TransY))
            return false;
        if (matrix.SkewX != 0 || matrix.SkewY != 0 || matrix.Persp0 != 0 || matrix.Persp1 != 0 || matrix.Persp2 != 1)
            return false;

        float relativeDifference = MathF.Abs(scaleX - scaleY) / MathF.Max(scaleX, scaleY);
        return relativeDifference < 0.001f;
    }

    private void EnsureTargets(int width, int height, bool antiAlias)
    {
        if (_renderTarget is not null && _width == width && _height == height && _antiAlias == antiAlias)
            return;

        DisposeTargets();
        _width = width;
        _height = height;
        _antiAlias = antiAlias;
        ID3D12Device device = _shared!.Device;
        uint sampleCount = antiAlias ? (uint)D3D12SharedDevice.MsaaSampleCount : 1u;
        _renderTarget = device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, sampleCount, 0, ResourceFlags.AllowRenderTarget, TextureLayout.Unknown, 0),
            ResourceStates.RenderTarget,
            null);
        if (antiAlias)
        {
            _resolveTarget = device.CreateCommittedResource(
                HeapType.Default,
                HeapFlags.None,
                ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.None, TextureLayout.Unknown, 0),
                ResourceStates.ResolveDest,
                null);
        }
        _rtvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1, DescriptorHeapFlags.None, 0));
        _rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        device.CreateRenderTargetView(_renderTarget, null, _rtvHandle);

        _readbackRowPitch = (width * 4 + 255) & ~255;
        _readback = device.CreateCommittedResource(
            HeapType.Readback,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)_readbackRowPitch * (uint)height),
            ResourceStates.CopyDest,
            null);
        _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
    }

    private void EnsureUpload(int byteLength)
    {
        if (_vertexUpload is not null && _uploadBytes >= byteLength)
            return;

        if (_vertexUpload is not null)
        {
            _vertexUpload.Unmap(0);
            _vertexUpload.Dispose();
        }

        long roundedLength = Math.Max(1 << 20, ((long)byteLength + 65535) & ~65535L);
        _uploadBytes = (int)roundedLength;
        _vertexUpload = _shared!.Device.CreateCommittedResource(
            HeapType.Upload,
            HeapFlags.None,
            ResourceDescription.Buffer((ulong)_uploadBytes),
            ResourceStates.GenericRead,
            null);
        void* pointer = null;
        _vertexUpload.Map(0, &pointer).CheckError();
        _uploadCpu = (nint)pointer;
    }

    private void CopyToBitmap()
    {
        byte* source = _readback!.Map<byte>(0);
        try
        {
            byte* destination = (byte*)_bitmap!.GetPixels();
            int rowBytes = _width * 4;
            for (int y = 0; y < _height; y++)
            {
                Buffer.MemoryCopy(source + (long)y * _readbackRowPitch, destination + (long)y * rowBytes, rowBytes, rowBytes);
            }
        }
        finally
        {
            _readback.Unmap(0);
        }
    }

    private void WaitForFence(ulong value)
    {
        if (_fence!.CompletedValue >= value)
            return;

        _fence.SetEventOnCompletion(value, _fenceEvent!.SafeWaitHandle.DangerousGetHandle()).CheckError();
        while (_fence.CompletedValue < value)
        {
            if (!_fenceEvent.WaitOne(100))
                _shared!.Device.DeviceRemovedReason.CheckError();
        }
    }

    private void DisposeTargets()
    {
        _bitmap?.Dispose();
        _bitmap = null;
        _readback?.Dispose();
        _readback = null;
        _resolveTarget?.Dispose();
        _resolveTarget = null;
        _rtvHeap?.Dispose();
        _rtvHeap = null;
        _renderTarget?.Dispose();
        _renderTarget = null;
    }

    public void Dispose()
    {
        if (_fenceValue > 0 && _fence is not null)
        {
            try
            {
                WaitForFence(_fenceValue);
            }
            catch
            {
            }
        }

        DisposeTargets();
        if (_vertexUpload is not null)
        {
            _vertexUpload.Unmap(0);
            _vertexUpload.Dispose();
            _vertexUpload = null;
        }

        _commandList?.Dispose();
        _commandList = null;
        _allocator?.Dispose();
        _allocator = null;
        _fence?.Dispose();
        _fence = null;
        _fenceEvent?.Dispose();
        _fenceEvent = null;
        _queue?.Dispose();
        _queue = null;
        _shared?.Release();
        _shared = null;
    }
}
