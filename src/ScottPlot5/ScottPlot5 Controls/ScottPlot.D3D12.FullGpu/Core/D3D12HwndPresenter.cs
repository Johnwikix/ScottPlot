using ScottPlot.D3D12.Rendering.Internal;
using Vortice.Direct3D12;
using Vortice.DXGI;

namespace ScottPlot.D3D12.Rendering;

/// <summary>Presents resolved complete Figures directly to a native child window without pixel readback.</summary>
/// <remarks>The caller owns the window and renderer and must dispose this presenter before either of them.</remarks>
public sealed class D3D12HwndPresenter : IDisposable
{
    private readonly D3D12Renderer Renderer;
    private readonly nint Window;
    private readonly ID3D12CommandAllocator Allocator;
    private readonly ID3D12GraphicsCommandList List;
    private IDXGISwapChain3? SwapChain;
    private int Width;
    private int Height;
    private bool Disposed;

    /// <summary>Gets the number of swapchain creations and buffer resizes.</summary>
    public int ResizeCount { get; private set; }

    /// <summary>Gets the number of completed presentation submissions.</summary>
    public long PresentedFrames { get; private set; }

    /// <summary>Initializes a new instance of the <see cref="D3D12HwndPresenter"/> class.</summary>
    /// <param name="renderer">The renderer providing the resolved GPU texture.</param>
    /// <param name="window">The live destination window handle.</param>
    /// <exception cref="ArgumentNullException">The renderer or window handle is null.</exception>
    public D3D12HwndPresenter(D3D12Renderer renderer, nint window)
    {
        Renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        Renderer.Device.VerifyAccess();
        if (window == 0)
            throw new ArgumentNullException(nameof(window));

        Window = window;
        Allocator = Renderer.Device.NativeDevice.CreateCommandAllocator(CommandListType.Direct);
        ID3D12GraphicsCommandList? list = null;
        try
        {
            list = Renderer.Device.NativeDevice.CreateCommandList<ID3D12GraphicsCommandList>(0, CommandListType.Direct, Allocator);
            list.Close();
            List = list;
        }
        catch
        {
            list?.Dispose();
            Allocator.Dispose();
            throw;
        }
    }

    /// <summary>Copies the rendered viewport on the GPU and presents it to the window.</summary>
    /// <param name="width">The physical client width used for the preceding render.</param>
    /// <param name="height">The physical client height used for the preceding render.</param>
    /// <param name="synchronizeToDisplay"><see langword="true"/> to synchronize presentation to the display; otherwise, <see langword="false"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A viewport dimension is not positive.</exception>
    /// <exception cref="InvalidOperationException">The renderer has no matching completed viewport.</exception>
    /// <exception cref="ObjectDisposedException">The presenter has been disposed.</exception>
    public void Present(int width, int height, bool synchronizeToDisplay = true)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Renderer.Device.VerifyAccess();
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "A positive physical viewport is required.");
        if (width != Renderer.LastFrame.Width || height != Renderer.LastFrame.Height)
            throw new InvalidOperationException("Render the current full viewport before presenting it.");

        EnsureSwapChain(width, height);
        using ID3D12Resource buffer = SwapChain!.GetBuffer<ID3D12Resource>(SwapChain.CurrentBackBufferIndex);
        Allocator.Reset();
        List.Reset(Allocator);
        GpuCommands.Transition(List, Renderer.ResolvedTexture, ResourceStates.RenderTarget, ResourceStates.CopySource);
        GpuCommands.Transition(List, buffer, ResourceStates.Present, ResourceStates.CopyDest);
        List.CopyTextureRegion(new TextureCopyLocation(buffer, 0), 0, 0, 0,
            new TextureCopyLocation(Renderer.ResolvedTexture, 0), new Vortice.Mathematics.Box(0, 0, 0, width, height, 1));
        GpuCommands.Transition(List, Renderer.ResolvedTexture, ResourceStates.CopySource, ResourceStates.RenderTarget);
        GpuCommands.Transition(List, buffer, ResourceStates.CopyDest, ResourceStates.Present);
        List.Close();
        GpuCommands.Execute(Renderer.Device.CommandQueue, List);
        try
        {
            SwapChain.Present(synchronizeToDisplay ? 1u : 0u, PresentFlags.None).CheckError();
        }
        finally
        {
            // Even a failed Present must finish the submitted copy before allocator reuse or disposal.
            if (!Renderer.Device.IsDeviceLost)
                Renderer.Device.WaitForIdle();
        }
        PresentedFrames++;
    }

    private void EnsureSwapChain(int width, int height)
    {
        if (SwapChain is not null && Width == width && Height == height)
            return;

        Renderer.Device.WaitForIdle();
        if (SwapChain is null)
        {
            using IDXGISwapChain1 chain = Renderer.Device.Factory.CreateSwapChainForHwnd(Renderer.Device.CommandQueue, Window,
                new SwapChainDescription1
                {
                    Width = (uint)width,
                    Height = (uint)height,
                    Format = Format.B8G8R8A8_UNorm,
                    BufferCount = 2,
                    BufferUsage = Usage.RenderTargetOutput,
                    SampleDescription = new SampleDescription(1, 0),
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipDiscard,
                    AlphaMode = AlphaMode.Ignore,
                });
            // Prevent Alt+Enter from changing a child control into an exclusive fullscreen swapchain.
            Renderer.Device.Factory.MakeWindowAssociation(Window, WindowAssociationFlags.IgnoreAltEnter).CheckError();
            SwapChain = chain.QueryInterface<IDXGISwapChain3>();
        }
        else
        {
            SwapChain.ResizeBuffers(2, (uint)width, (uint)height, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        }

        Width = width;
        Height = height;
        ResizeCount++;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Disposed)
            return;

        Renderer.Device.VerifyOwnerThread();
        try
        {
            if (!Renderer.Device.IsDeviceLost)
                Renderer.Device.WaitForIdle();
        }
        finally
        {
            SwapChain?.Dispose();
            SwapChain = null;
            List.Dispose();
            Allocator.Dispose();
            Disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}
