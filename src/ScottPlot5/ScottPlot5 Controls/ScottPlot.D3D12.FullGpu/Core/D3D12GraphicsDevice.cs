using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

namespace ScottPlot.D3D12.Rendering;

/// <summary>Owns one thread-affine hardware D3D12 device, queue, Skia GPU context and verified completion fence.</summary>
/// <remarks>No WARP adapter or raster-surface fallback is created. Native device/context properties are borrowed and must not be disposed by callers.</remarks>
public sealed class D3D12GraphicsDevice : IDisposable
{
    [ThreadStatic] private static D3D12GraphicsDevice? Shared;
    private readonly int OwnerThread = Environment.CurrentManagedThreadId;
    private readonly AutoResetEvent FenceEvent = new(false);
    private ID3D12Fence? Fence;
    private ulong FenceValue;
    private int Leases;
    private bool Disposed;
    private IDXGIFactory4? FactoryObject;
    private IDXGIAdapter1? AdapterObject;
    private ID3D12Device? DeviceObject;
    private ID3D12CommandQueue? QueueObject;
    private GRContext? ContextObject;

    /// <summary>Gets the borrowed DXGI factory.</summary>
    public IDXGIFactory4 Factory => FactoryObject ?? throw new ObjectDisposedException(nameof(D3D12GraphicsDevice));
    /// <summary>Gets the borrowed hardware adapter.</summary>
    public IDXGIAdapter1 Adapter => AdapterObject ?? throw new ObjectDisposedException(nameof(D3D12GraphicsDevice));
    /// <summary>Gets the borrowed native D3D12 device.</summary>
    public ID3D12Device NativeDevice => DeviceObject ?? throw new ObjectDisposedException(nameof(D3D12GraphicsDevice));
    /// <summary>Gets the borrowed direct command queue used by Skia and all presentation copies.</summary>
    public ID3D12CommandQueue CommandQueue => QueueObject ?? throw new ObjectDisposedException(nameof(D3D12GraphicsDevice));
    /// <summary>Gets the borrowed hardware Direct3D Skia context.</summary>
    public GRContext Context => ContextObject ?? throw new ObjectDisposedException(nameof(D3D12GraphicsDevice));
    /// <summary>Gets the hardware adapter description.</summary>
    public string AdapterName => Adapter.Description1.Description;
    /// <summary>Gets whether the device/context is lost or has been disposed; no CPU fallback is attempted.</summary>
    public bool IsDeviceLost => Disposed || ContextObject?.IsAbandoned == true || DeviceObject?.DeviceRemovedReason.Failure == true;
    internal void VerifyOwnerThread() { if (Environment.CurrentManagedThreadId != OwnerThread) throw new InvalidOperationException("Access GPU resources on the owning thread."); }
    /// <summary>Gets the maximum supported BGRA surface sample count.</summary>
    public int MaximumBgraSampleCount => Context.GetMaxSurfaceSampleCount(SKColorType.Bgra8888);

    /// <summary>Initializes a hardware Direct3D device and GPU context on the calling thread.</summary>
    /// <exception cref="PlatformNotSupportedException">A hardware D3D12/MSAA4 configuration is unavailable.</exception>
    public D3D12GraphicsDevice()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
                throw new PlatformNotSupportedException("ScottPlot.D3D12 requires Windows 10 version 1809 or later.");
            FactoryObject = CreateDXGIFactory2<IDXGIFactory4>(false);
            FactoryObject.EnumAdapters1(0, out IDXGIAdapter1 adapter).CheckError();
            AdapterObject = adapter;
            if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
                throw new PlatformNotSupportedException("ScottPlot.D3D12 requires a hardware adapter; software rendering is disabled.");
            DeviceObject = D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0);
            QueueObject = DeviceObject.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
            Fence = DeviceObject.CreateFence(0);
            using GRD3DBackendContext backend = new() { Adapter = adapter.NativePointer, Device = DeviceObject.NativePointer, Queue = QueueObject.NativePointer };
            ContextObject = GRContext.CreateDirect3D(backend) ?? throw new PlatformNotSupportedException("Skia could not create a D3D12 GPU context.");
            if (ContextObject.Backend != GRBackend.Direct3D || MaximumBgraSampleCount < 4)
                throw new PlatformNotSupportedException("ScottPlot.D3D12 requires Direct3D with BGRA MSAA4; no fallback is available.");
            ContextObject.SetResourceCacheLimit(64L * 1024 * 1024);
        }
        catch { DisposeNative(); throw; }
    }

    /// <summary>Acquires a reference-counted device shared by controls on the current UI thread.</summary>
    /// <returns>A lease whose disposal releases this caller's ownership.</returns>
    public static D3D12DeviceLease AcquireShared()
    {
        D3D12GraphicsDevice device = Shared is { Disposed: false, IsDeviceLost: false } ? Shared : Shared = new();
        device.VerifyAccess(); device.Leases++;
        return new D3D12DeviceLease(device);
    }

    /// <summary>Throws if the caller is on another thread, the device is disposed, or its GPU context is lost.</summary>
    public void VerifyAccess()
    {
        if (Environment.CurrentManagedThreadId != OwnerThread) throw new InvalidOperationException("D3D12 rendering must occur on its owning thread.");
        ObjectDisposedException.ThrowIf(Disposed, this);
        NativeDevice.DeviceRemovedReason.CheckError();
        if (Context.IsAbandoned) throw new InvalidOperationException("The Skia D3D12 GPU context was lost; CPU fallback is disabled.");
    }

    /// <summary>Signals this queue and waits until the actual fence value completes, checking after every event wake.</summary>
    public void WaitForIdle()
    {
        VerifyAccess();
        ulong target = ++FenceValue;
        CommandQueue.Signal(Fence!, target).CheckError();
        long deadline = Environment.TickCount64 + 20_000;
        while (Fence!.CompletedValue < target)
        {
            NativeDevice.DeviceRemovedReason.CheckError();
            Fence.SetEventOnCompletion(target, FenceEvent.SafeWaitHandle.DangerousGetHandle()).CheckError();
            int remaining = (int)Math.Clamp(deadline - Environment.TickCount64, 0, 20_000);
            if (remaining == 0 || !FenceEvent.WaitOne(remaining)) throw new TimeoutException("D3D12 GPU completion fence timed out.");
        }
        NativeDevice.DeviceRemovedReason.CheckError();
    }

    internal void ReleaseLease()
    {
        if (Environment.CurrentManagedThreadId != OwnerThread) throw new InvalidOperationException("Release the lease on its owning thread.");
        if (--Leases != 0) return;
        if (ReferenceEquals(Shared, this)) Shared = null;
        Dispose();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Disposed) return;
        if (Environment.CurrentManagedThreadId != OwnerThread) throw new InvalidOperationException("Dispose the device on its owning thread.");
        if (Leases != 0) throw new InvalidOperationException("Release all shared device leases before disposing the device.");
        try { if (!IsDeviceLost) { Context.Flush(true, false); WaitForIdle(); } }
        catch (Exception) when (IsDeviceLost) { }
        finally { DisposeNative(); }
        GC.SuppressFinalize(this);
    }

    private void DisposeNative()
    {
        Disposed = true;
        ContextObject?.Dispose(); ContextObject = null;
        Fence?.Dispose(); Fence = null; FenceEvent.Dispose();
        QueueObject?.Dispose(); QueueObject = null;
        DeviceObject?.Dispose(); DeviceObject = null;
        AdapterObject?.Dispose(); AdapterObject = null;
        FactoryObject?.Dispose(); FactoryObject = null;
    }
}

/// <summary>Holds one UI-thread reference to a shared D3D12 graphics device.</summary>
public sealed class D3D12DeviceLease : IDisposable
{
    private D3D12GraphicsDevice? Owner;
    internal D3D12DeviceLease(D3D12GraphicsDevice owner) => Owner = owner;
    /// <summary>Gets the shared device while this lease is alive.</summary>
    public D3D12GraphicsDevice Device => Owner ?? throw new ObjectDisposedException(nameof(D3D12DeviceLease));
    /// <inheritdoc />
    public void Dispose() { D3D12GraphicsDevice? owner = Owner; if (owner is null) return; owner.ReleaseLease(); Owner = null; }
}



