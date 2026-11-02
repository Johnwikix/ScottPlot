using Avalonia;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using ScottPlot.D3D12.Rendering;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using AvaPixelSize = global::Avalonia.PixelSize;

namespace ScottPlot.Avalonia.D3D12.Internal;

/// <summary>Copies a resolved D3D12 Figure into a keyed-mutex texture consumed by Avalonia.</summary>
/// <remarks>Owns no staging textures or CPU pixel buffers. All methods run on the renderer's owner thread.</remarks>
internal sealed class SharedTexturePresenter : IAsyncDisposable
{
    private readonly D3D12Renderer Renderer;
    private readonly ICompositionGpuInterop Interop;
    private readonly CompositionDrawingSurface Surface;
    private readonly ID3D11Device Native11;
    private readonly ID3D11DeviceContext NativeContext;
    private readonly ID3D11Device On12Device;
    private readonly ID3D11DeviceContext On12Context;
    private readonly D3D11On12Interop.On12Device On12;
    private ID3D11Resource? Wrapped;
    private nint BoundResource;
    private ID3D11Texture2D? Texture;
    private ID3D11Texture2D? Opened;
    private IDXGIKeyedMutex? Mutex;
    private ICompositionImportedGpuImage? Imported;
    private Task Pending = Task.CompletedTask;
    private AvaPixelSize Size;
    internal int TextureAllocations { get; private set; }
    internal long Copies { get; private set; }
    internal long CompletedUpdates { get; private set; }

    internal SharedTexturePresenter(D3D12Renderer renderer, ICompositionGpuInterop interop, CompositionDrawingSurface surface)
    {
        Renderer = renderer; Interop = interop; Surface = surface;
        string handleType = KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle;
        if (!interop.SupportedImageHandleTypes.Contains(handleType) ||
            !interop.GetSynchronizationCapabilities(handleType).HasFlag(CompositionGpuImportedImageSynchronizationCapabilities.KeyedMutex))
            throw new PlatformNotSupportedException("Avalonia must support D3D11 shared images with keyed mutex synchronization; no CPU fallback.");
        long luid = renderer.Device.Adapter.Description1.Luid;
        if (interop.DeviceLuid is not { Length: 8 } bytes || BitConverter.ToInt64(bytes) != luid)
            throw new PlatformNotSupportedException("D3D12 and Avalonia must use the same adapter LUID; cross-adapter copies are disabled.");
        D3D11.D3D11CreateDevice(renderer.Device.Adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
            [FeatureLevel.Level_11_0], out Native11, out NativeContext).CheckError();
        try
        {
            (On12Device, On12Context, On12) = D3D11On12Interop.Create(renderer.Device.NativeDevice.NativePointer,
                renderer.Device.CommandQueue.NativePointer);
        }
        catch { NativeContext.Dispose(); Native11.Dispose(); throw; }
    }

    internal async Task PresentAsync(AvaPixelSize size, CancellationToken cancellationToken)
    {
        await Pending.WaitAsync(cancellationToken);
        if (Size != size) await ResizeAsync(size);
        nint resource = Renderer.ResolvedTexture.NativePointer;
        if (BoundResource != resource)
        {
            Wrapped?.Dispose();
            Wrapped = On12.CreateWrappedResource(Renderer.ResolvedTexture,
                Vortice.Direct3D12.ResourceStates.RenderTarget, Vortice.Direct3D12.ResourceStates.RenderTarget);
            On12.ReleaseWrappedResources(Wrapped); On12Context.Flush();
            BoundResource = resource;
        }
        // The mutex belongs to the D3D11 shared output, not the wrapped D3D12 source.
        AcquireOutput();
        try
        {
            On12.AcquireWrappedResources(Wrapped!);
            try
            {
                On12Context.CopySubresourceRegion(Opened!, 0, 0, 0, 0, Wrapped!, 0,
                    new Vortice.Mathematics.Box(0, 0, 0, size.Width, size.Height, 1));
                Copies++;
            }
            finally { On12.ReleaseWrappedResources(Wrapped!); On12Context.Flush(); }
        }
        finally { Mutex!.ReleaseSync(1); }
        Pending = Surface.UpdateWithKeyedMutexAsync(Imported!, 1, 0);
        // Capture the batch containing this update before awaiting it. Asking for another
        // batch after import completion would charge an extra compositor tick to D3D12.
        Task rendered = Surface.Compositor.RequestCompositionBatchCommitAsync().Rendered;
        await Task.WhenAll(Pending, rendered).WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        CompletedUpdates++;
    }

    private async Task ResizeAsync(AvaPixelSize size)
    {
        await ReleaseImageAsync();
        // A native D3D11 device creates the mutex resource; 11On12 opens it for the GPU copy.
        Texture = Native11.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)size.Width, Height = (uint)size.Height, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
            CPUAccessFlags = CpuAccessFlags.None, MiscFlags = ResourceOptionFlags.SharedKeyedMutex,
        });
        using IDXGIResource dxgi = Texture.QueryInterface<IDXGIResource>();
        nint handle = dxgi.SharedHandle;
        Opened = On12Device.OpenSharedResource<ID3D11Texture2D>(handle);
        Mutex = Opened.QueryInterface<IDXGIKeyedMutex>();
        Imported = Interop.ImportImage(new PlatformHandle(handle,
                KnownPlatformGraphicsExternalImageHandleTypes.D3D11TextureGlobalSharedHandle),
            new PlatformGraphicsExternalImageProperties
            {
                Width = size.Width, Height = size.Height, Format = PlatformGraphicsExternalImageFormat.B8G8R8A8UNorm,
                TopLeftOrigin = true,
            });
        await Imported.ImportCompleted.WaitAsync(TimeSpan.FromSeconds(20));
        Size = size; TextureAllocations++;
    }

    private async Task ReleaseImageAsync()
    {
        try { await Pending; }
        catch when (Pending.IsFaulted || Pending.IsCanceled) { /* Still release all owned resources after a failed import. */ }
        try
        {
            var imported = Imported; Imported = null;
            if (imported is not null) await imported.DisposeAsync();
        }
        finally
        {
            Mutex?.Dispose(); Mutex = null;
            Opened?.Dispose(); Opened = null;
            Texture?.Dispose(); Texture = null;
            Size = default;
        }
    }

    private unsafe void AcquireOutput()
    {
        // IDXGIKeyedMutex::AcquireSync is slot 8. WAIT_TIMEOUT is a positive HRESULT,
        // so a generated void/CheckError wrapper cannot distinguish it from S_OK.
        nint pointer = Mutex!.NativePointer;
        var acquire = (delegate* unmanaged[Stdcall]<nint, ulong, uint, int>)(*(nint**)pointer)[8];
        int result = acquire(pointer, 0, 10_000);
        if (result != 0) throw new InvalidOperationException($"Keyed mutex acquire failed or timed out: 0x{result:X8}");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        try { await ReleaseImageAsync(); }
        finally
        {
            try { if (!Renderer.Device.IsDeviceLost) Renderer.Device.WaitForIdle(); }
            finally
            {
                Wrapped?.Dispose(); Wrapped = null;
                On12.Dispose(); On12Context.Dispose(); On12Device.Dispose();
                NativeContext.Dispose(); Native11.Dispose();
            }
        }
    }
}

