using ScottPlot.Plottables;
using SharpGen.Runtime;
using SkiaSharp;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.Direct3D12.D3D12;
using static Vortice.Direct3D9.D3D9;
using D3D11Device = Vortice.Direct3D11.ID3D11Device;
using D3D11DeviceContext = Vortice.Direct3D11.ID3D11DeviceContext;
using D3D11Resource = Vortice.Direct3D11.ID3D11Resource;
using D3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;
using D3D11Texture2DDescription = Vortice.Direct3D11.Texture2DDescription;

namespace ScottPlot.D3D12.Rendering;

internal sealed unsafe class D3D12NativePresenter : IDisposable
{
    private const int VertexStride = 24;

    private readonly struct CurvePixels(D3D12SignalXY signal, IReadOnlyList<Pixel> pixels)
    {
        public D3D12SignalXY Signal { get; } = signal;
        public IReadOnlyList<Pixel> Pixels { get; } = pixels;
    }

    private readonly System.Windows.Controls.Image Host;
    private readonly Action Invalidate;
    private readonly D3DImage Image = new();
    private D3D12SharedDevice? Shared;
    private ID3D12CommandQueue? Queue;
    private ID3D12CommandAllocator? Allocator;
    private ID3D12GraphicsCommandList? CommandList;
    private ID3D12Fence? Fence;
    private ManualResetEvent? FenceEvent;
    private ulong FenceValue;

    private D3D11Device? Device11;
    private D3D11DeviceContext? Context11;
    private D3D11On12Interop.On12Device? On12;
    private Vortice.Direct3D9.IDirect3D9Ex? D3D9;
    private Vortice.Direct3D9.IDirect3DDevice9Ex? Device9;
    private D3D11Texture2D? Present11;
    private D3D11Resource? WrappedPresent;
    private Vortice.Direct3D9.IDirect3DTexture9? Texture9;
    private Vortice.Direct3D9.IDirect3DSurface9? Surface9;

    private ID3D12Resource? MsaaTarget;
    private ID3D12Resource? PresentTarget;
    private ID3D12DescriptorHeap? RtvHeap;
    private CpuDescriptorHandle RtvHandle;
    private ID3D12Resource? VertexUpload;
    private nint UploadCpu;
    private int UploadBytes;
    private readonly float[] ViewConstants = new float[4];
    private int Width;
    private int Height;
    private bool AntiAlias;
    private bool NeedsRecreate;
    private bool FrontBufferAvailable = true;
    private bool IsBroken;

    public D3D12NativePresenter(System.Windows.Controls.Image host, Action invalidate)
    {
        Host = host;
        Invalidate = invalidate;
        Host.Source = Image;
        Host.Visibility = Visibility.Collapsed;
        Image.IsFrontBufferAvailableChanged += OnFrontBufferAvailableChanged;

        try
        {
            Shared = D3D12SharedDevice.Acquire();
            if (Shared is null)
            {
                IsBroken = true;
                return;
            }

            Queue = Shared.Device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct, 0, CommandQueueFlags.None, 0));
            Allocator = Shared.Device.CreateCommandAllocator(CommandListType.Direct);
            CommandList = Shared.Device.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, Allocator, Shared.PipelineState);
            CommandList.Close();
            Fence = Shared.Device.CreateFence(0, FenceFlags.None);
            FenceEvent = new ManualResetEvent(false);
            (Device11, Context11, On12) = D3D11On12Interop.Create(Shared.Device.NativePointer, Queue.NativePointer);
            CreateD3D9Device();
        }
        catch
        {
            IsBroken = true;
            Dispose();
        }
    }

    public bool Render(RenderPack rp, IReadOnlyList<D3D12SignalXY> signals)
    {
        if (IsBroken || !FrontBufferAvailable || Shared is null)
            return false;

        try
        {
            return RenderCore(rp, signals);
        }
        catch
        {
            IsBroken = true;
            Clear();
            return false;
        }
    }

    private bool RenderCore(RenderPack rp, IReadOnlyList<D3D12SignalXY> signals)
    {
        SKMatrix matrix = rp.Canvas.TotalMatrix;
        if (!TryGetScale(matrix, out float scaleX, out float scaleY))
            return false;
        if (rp.FigureRect.Left != 0 || rp.FigureRect.Top != 0)
            return false;

        int width = (int)Math.Ceiling(rp.FigureRect.Width);
        int height = (int)Math.Ceiling(rp.FigureRect.Height);
        if (width < 8 || height < 8 || width > 16384 || height > 16384)
            return false;

        bool antiAlias = signals[0].LineStyle.AntiAlias;
        EnsureTargets(width, height, antiAlias);
        List<CurvePixels> curves = new(signals.Count);
        long vertexCapacity = 0;
        foreach (D3D12SignalXY signal in signals)
        {
            signal.Axes.DataRect = rp.DataRect;
            IReadOnlyList<Pixel> pixels = signal.Data.GetPixelsToDraw(rp, signal.Axes, ConnectStyle.Straight);
            if (pixels.Count < 2)
                continue;
            curves.Add(new CurvePixels(signal, pixels));
            vertexCapacity += (long)(pixels.Count - 1) * 12;
        }

        if (curves.Count == 0)
        {
            Clear();
            return true;
        }

        long uploadLength = vertexCapacity * VertexStride;
        if (uploadLength <= 0 || uploadLength > int.MaxValue - 65535L)
            return false;
        EnsureUpload((int)uploadLength);

        int[] starts = new int[curves.Count];
        int[] counts = new int[curves.Count];
        int totalVertices = 0;
        for (int i = 0; i < curves.Count; i++)
        {
            starts[i] = totalVertices;
            totalVertices += D3D12CurveRenderer.WriteCurve(curves[i].Signal, curves[i].Pixels, scaleX, scaleY, matrix.TransX, matrix.TransY, UploadCpu + totalVertices * VertexStride);
            counts[i] = totalVertices - starts[i];
        }

        if (totalVertices == 0)
        {
            Clear();
            return true;
        }

        ID3D12GraphicsCommandList list = CommandList!;
        ID3D12PipelineState pipelineState = antiAlias ? Shared!.PipelineStateMsaa : Shared!.PipelineState;
        Allocator!.Reset();
        list.Reset(Allocator, pipelineState);
        ResourceStates presentRenderState = antiAlias ? ResourceStates.ResolveDest : ResourceStates.RenderTarget;
        Transition(list, PresentTarget!, ResourceStates.CopySource, presentRenderState);
        list.RSSetViewports(new Viewport(0, 0, width, height));
        list.RSSetScissorRects([GetDataScissor(rp.DataRect, matrix, width, height)]);
        list.SetGraphicsRootSignature(Shared!.RootSignature);
        list.OMSetRenderTargets(RtvHandle, null);
        list.ClearRenderTargetView(RtvHandle, new Color4(0, 0, 0, 0));
        ViewConstants[0] = width;
        ViewConstants[1] = height;
        list.SetGraphicsRoot32BitConstants(0, ViewConstants, 0);
        list.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        list.IASetVertexBuffers(0, new VertexBufferView(VertexUpload!.GPUVirtualAddress, (uint)(totalVertices * VertexStride), VertexStride));
        for (int i = 0; i < curves.Count; i++)
        {
            if (counts[i] > 0)
                list.DrawInstanced((uint)counts[i], 1, (uint)starts[i], 0);
        }

        if (antiAlias)
        {
            Transition(list, MsaaTarget!, ResourceStates.RenderTarget, ResourceStates.ResolveSource);
            list.ResolveSubresource(PresentTarget!, 0, MsaaTarget!, 0, Format.B8G8R8A8_UNorm);
            Transition(list, MsaaTarget!, ResourceStates.ResolveSource, ResourceStates.RenderTarget);
        }

        Transition(list, PresentTarget!, presentRenderState, ResourceStates.CopySource);
        list.Close();
        Queue!.ExecuteCommandLists([list]);
        On12!.AcquireWrappedResources(WrappedPresent!);
        Context11!.CopyResource(Present11!, WrappedPresent!);
        On12.ReleaseWrappedResources(WrappedPresent!);
        Context11.Flush();
        Queue.Signal(Fence!, ++FenceValue).CheckError();
        WaitForFence(FenceValue);

        Image.Lock();
        try
        {
            Image.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally
        {
            Image.Unlock();
        }

        Host.Visibility = Visibility.Visible;
        return true;
    }

    public void Clear() => Host.Visibility = Visibility.Collapsed;

    private static global::Vortice.RawRect GetDataScissor(PixelRect dataRect, SKMatrix matrix, int width, int height)
    {
        int left = Math.Clamp((int)Math.Floor(dataRect.Left * matrix.ScaleX + matrix.TransX) - 1, 0, width);
        int top = Math.Clamp((int)Math.Floor(dataRect.Top * matrix.ScaleY + matrix.TransY) - 1, 0, height);
        int right = Math.Clamp((int)Math.Ceiling(dataRect.Right * matrix.ScaleX + matrix.TransX) + 1, 0, width);
        int bottom = Math.Clamp((int)Math.Ceiling(dataRect.Bottom * matrix.ScaleY + matrix.TransY) + 1, 0, height);
        return new global::Vortice.RawRect(left, top, right, bottom);
    }

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
        if (PresentTarget is not null && Width == width && Height == height && AntiAlias == antiAlias && !NeedsRecreate)
            return;

        DisposeTargets();
        NeedsRecreate = false;
        Width = width;
        Height = height;
        AntiAlias = antiAlias;
        ID3D12Device device = Shared!.Device;
        PresentTarget = device.CreateCommittedResource(
            HeapType.Default,
            HeapFlags.None,
            ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget, TextureLayout.Unknown, 0),
            ResourceStates.CopySource,
            null);

        ID3D12Resource renderTarget = PresentTarget;
        if (antiAlias)
        {
            MsaaTarget = device.CreateCommittedResource(
                HeapType.Default,
                HeapFlags.None,
                ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, D3D12SharedDevice.MsaaSampleCount, 0, ResourceFlags.AllowRenderTarget, TextureLayout.Unknown, 0),
                ResourceStates.RenderTarget,
                null);
            renderTarget = MsaaTarget;
        }

        RtvHeap = device.CreateDescriptorHeap(new DescriptorHeapDescription(DescriptorHeapType.RenderTargetView, 1, DescriptorHeapFlags.None, 0));
        RtvHandle = RtvHeap.GetCPUDescriptorHandleForHeapStart();
        device.CreateRenderTargetView(renderTarget, null, RtvHandle);
        WrappedPresent = On12!.CreateWrappedResource(PresentTarget, ResourceStates.CopySource, ResourceStates.CopySource);
        On12.ReleaseWrappedResources(WrappedPresent);
        Context11!.Flush();

        D3D11Texture2DDescription present = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = Vortice.Direct3D11.ResourceUsage.Default,
            BindFlags = Vortice.Direct3D11.BindFlags.RenderTarget | Vortice.Direct3D11.BindFlags.ShaderResource,
            MiscFlags = Vortice.Direct3D11.ResourceOptionFlags.Shared,
        };
        Present11 = Device11!.CreateTexture2D(present);
        using IDXGIResource dxgiResource = Present11.QueryInterface<IDXGIResource>();
        nint sharedHandle = dxgiResource.SharedHandle;
        Texture9 = Device9!.CreateTexture((uint)width, (uint)height, 1, Vortice.Direct3D9.Usage.RenderTarget, Vortice.Direct3D9.Format.A8R8G8B8, Vortice.Direct3D9.Pool.Default, ref sharedHandle);
        Surface9 = Texture9.GetSurfaceLevel(0);

        Image.Lock();
        try
        {
            Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, Surface9.NativePointer, true);
        }
        finally
        {
            Image.Unlock();
        }
    }

    private void EnsureUpload(int byteLength)
    {
        if (VertexUpload is not null && UploadBytes >= byteLength)
            return;
        if (VertexUpload is not null)
        {
            VertexUpload.Unmap(0);
            VertexUpload.Dispose();
        }

        long roundedLength = Math.Max(1 << 20, ((long)byteLength + 65535) & ~65535L);
        UploadBytes = (int)roundedLength;
        VertexUpload = Shared!.Device.CreateCommittedResource(HeapType.Upload, HeapFlags.None, ResourceDescription.Buffer((ulong)UploadBytes), ResourceStates.GenericRead, null);
        void* pointer = null;
        VertexUpload.Map(0, &pointer).CheckError();
        UploadCpu = (nint)pointer;
    }

    private void CreateD3D9Device()
    {
        D3D9 = Direct3DCreate9Ex() ?? throw new InvalidOperationException("D3D9Ex creation failed.");
        Vortice.Direct3D9.PresentParameters parameters = new()
        {
            Windowed = true,
            SwapEffect = Vortice.Direct3D9.SwapEffect.Discard,
            DeviceWindowHandle = GetDesktopWindow(),
            PresentationInterval = Vortice.Direct3D9.PresentInterval.Default,
        };
        Device9 = D3D9.CreateDeviceEx(
            0,
            Vortice.Direct3D9.DeviceType.Hardware,
            nint.Zero,
            Vortice.Direct3D9.CreateFlags.HardwareVertexProcessing | Vortice.Direct3D9.CreateFlags.Multithreaded | Vortice.Direct3D9.CreateFlags.FpuPreserve,
            parameters) ?? throw new InvalidOperationException("D3D9Ex device creation failed.");
    }

    private static void Transition(ID3D12GraphicsCommandList list, ID3D12Resource resource, ResourceStates before, ResourceStates after)
    {
        list.ResourceBarrier([ResourceBarrier.BarrierTransition(resource, before, after)]);
    }

    private void WaitForFence(ulong value)
    {
        if (Fence!.CompletedValue >= value)
            return;
        Fence.SetEventOnCompletion(value, FenceEvent!.SafeWaitHandle.DangerousGetHandle()).CheckError();
        while (Fence.CompletedValue < value)
        {
            if (!FenceEvent.WaitOne(100))
                Shared!.Device.DeviceRemovedReason.CheckError();
        }
    }

    private void OnFrontBufferAvailableChanged(object? sender, DependencyPropertyChangedEventArgs e)
    {
        FrontBufferAvailable = Image.IsFrontBufferAvailable;
        NeedsRecreate = true;
        if (!FrontBufferAvailable)
            Clear();
        Invalidate();
    }

    private void DisposeTargets()
    {
        if (FenceValue > 0 && Fence is not null)
        {
            try { WaitForFence(FenceValue); } catch { }
        }

        Image.Lock();
        try
        {
            Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, nint.Zero);
        }
        finally
        {
            Image.Unlock();
        }

        Surface9?.Dispose();
        Surface9 = null;
        Texture9?.Dispose();
        Texture9 = null;
        WrappedPresent?.Dispose();
        WrappedPresent = null;
        Present11?.Dispose();
        Present11 = null;
        RtvHeap?.Dispose();
        RtvHeap = null;
        MsaaTarget?.Dispose();
        MsaaTarget = null;
        PresentTarget?.Dispose();
        PresentTarget = null;
    }

    public void Dispose()
    {
        DisposeTargets();
        if (VertexUpload is not null)
        {
            VertexUpload.Unmap(0);
            VertexUpload.Dispose();
            VertexUpload = null;
        }

        CommandList?.Dispose();
        CommandList = null;
        Allocator?.Dispose();
        Allocator = null;
        Fence?.Dispose();
        Fence = null;
        FenceEvent?.Dispose();
        FenceEvent = null;
        Surface9?.Dispose();
        Texture9?.Dispose();
        WrappedPresent?.Dispose();
        Present11?.Dispose();
        On12?.Dispose();
        On12 = null;
        Context11?.Dispose();
        Context11 = null;
        Device11?.Dispose();
        Device11 = null;
        Device9?.Dispose();
        Device9 = null;
        D3D9?.Dispose();
        D3D9 = null;
        Queue?.Dispose();
        Queue = null;
        Shared?.Release();
        Shared = null;
        Image.IsFrontBufferAvailableChanged -= OnFrontBufferAvailableChanged;
        Host.Source = null;
    }

    [DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();
}
