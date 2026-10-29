using ScottPlot.D3D12.Rendering.Internal;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace ScottPlot.D3D12.Rendering;

/// <summary>Presents resolved full Figures through a native composition swapchain attached to a SwapChainPanel.</summary>
/// <remarks>The panel interface must expose native SetSwapChain in vtable slot3. WinUI and UWP adapters acquire their respective native interfaces.</remarks>
public sealed class D3D12SwapChainPresenter : IDisposable
{
    private readonly D3D12Renderer Renderer;
    private readonly ID3D12CommandAllocator Allocator;
    private readonly ID3D12GraphicsCommandList List;
    private nint NativePanel;
    private IDXGISwapChain3? SwapChain;
    private int Width, Height;
    private float ScaleX, ScaleY;
    private bool Disposed;
    /// <summary>Gets the number of swapchain resizes; the chain itself is retained.</summary>
    public int ResizeCount { get; private set; }
    /// <summary>Gets the number of successfully submitted presentation frames.</summary>
    public long PresentedFrames { get; private set; }

    /// <summary>Initializes a native composition presenter, taking its own reference to the panel interface.</summary>
    /// <param name="renderer">The GPU renderer whose resolved texture is presented.</param>
    /// <param name="nativePanelInterface">The queried WinUI or UWP native SwapChainPanel interface pointer.</param>
    public D3D12SwapChainPresenter(D3D12Renderer renderer, nint nativePanelInterface)
    {
        Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer)); renderer.Device.VerifyAccess();
        if (nativePanelInterface == 0) throw new ArgumentNullException(nameof(nativePanelInterface));
        NativePanel = nativePanelInterface; Marshal.AddRef(NativePanel);
        try
        {
            Allocator = renderer.Device.NativeDevice.CreateCommandAllocator(CommandListType.Direct);
            try { List = renderer.Device.NativeDevice.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, Allocator); List.Close(); }
            catch { Allocator.Dispose(); throw; }
        }
        catch { Marshal.Release(NativePanel); NativePanel = 0; throw; }
    }

    /// <summary>Copies the complete resolved viewport on the GPU and presents it using the panel's actual composition scales.</summary>
    /// <param name="width">The physical viewport width used for the preceding render.</param>
    /// <param name="height">The physical viewport height used for the preceding render.</param>
    /// <param name="compositionScaleX">The panel's horizontal composition scale.</param>
    /// <param name="compositionScaleY">The panel's vertical composition scale.</param>
    /// <param name="synchronizeToDisplay">Whether presentation is synchronized to the display.</param>
    public void Present(int width, int height, float compositionScaleX, float compositionScaleY, bool synchronizeToDisplay = true)
    {
        ObjectDisposedException.ThrowIf(Disposed, this); Renderer.Device.VerifyAccess();
        if (width != Renderer.LastFrame.Width || height != Renderer.LastFrame.Height)
            throw new InvalidOperationException("Render the full current viewport before presenting it.");
        if (!float.IsFinite(compositionScaleX) || !float.IsFinite(compositionScaleY) || compositionScaleX <= 0 || compositionScaleY <= 0)
            throw new ArgumentOutOfRangeException(nameof(compositionScaleX));
        EnsureSwapChain(width, height);
        if (ScaleX != compositionScaleX || ScaleY != compositionScaleY)
        {
            SwapChain!.MatrixTransform = System.Numerics.Matrix3x2.CreateScale(1 / compositionScaleX, 1 / compositionScaleY);
            ScaleX = compositionScaleX; ScaleY = compositionScaleY;
        }
        using ID3D12Resource buffer = SwapChain!.GetBuffer<ID3D12Resource>(SwapChain.CurrentBackBufferIndex);
        Allocator.Reset(); List.Reset(Allocator);
        GpuCommands.Transition(List, Renderer.ResolvedTexture, ResourceStates.RenderTarget, ResourceStates.CopySource);
        GpuCommands.Transition(List, buffer, ResourceStates.Present, ResourceStates.CopyDest);
        List.CopyTextureRegion(new TextureCopyLocation(buffer, 0), 0, 0, 0,
            new TextureCopyLocation(Renderer.ResolvedTexture, 0), new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));
        GpuCommands.Transition(List, Renderer.ResolvedTexture, ResourceStates.CopySource, ResourceStates.RenderTarget);
        GpuCommands.Transition(List, buffer, ResourceStates.CopyDest, ResourceStates.Present);
        List.Close(); GpuCommands.Execute(Renderer.Device.CommandQueue, List);
        SwapChain.Present(synchronizeToDisplay ? 1u : 0u, PresentFlags.None).CheckError();
        Renderer.Device.WaitForIdle(); PresentedFrames++;
    }

    private void EnsureSwapChain(int width, int height)
    {
        if (SwapChain is not null && Width == width && Height == height) return;
        Renderer.Device.WaitForIdle();
        if (SwapChain is null)
        {
            using IDXGISwapChain1 chain = Renderer.Device.Factory.CreateSwapChainForComposition(Renderer.Device.CommandQueue,
                new SwapChainDescription1 { Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm,
                    BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SampleDescription = new SampleDescription(1, 0),
                    Scaling = Scaling.Stretch, SwapEffect = SwapEffect.FlipSequential, AlphaMode = AlphaMode.Premultiplied });
            SwapChain = chain.QueryInterface<IDXGISwapChain3>();
            SetSwapChain(SwapChain.NativePointer);
        }
        else SwapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        Width = width; Height = height; ResizeCount++;
    }

    private unsafe void SetSwapChain(nint chain)
    {
        nint* vtable = *(nint**)NativePanel;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, int>)vtable[3])(NativePanel, chain));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Disposed) return;
        try { if (!Renderer.Device.IsDeviceLost) Renderer.Device.WaitForIdle(); SetSwapChain(0); }
        finally
        {
            SwapChain?.Dispose(); SwapChain = null; List.Dispose(); Allocator.Dispose();
            if (NativePanel != 0) { Marshal.Release(NativePanel); NativePanel = 0; }
            Disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}



