using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using ScottPlot.D3D12.Rendering;
using Vortice.Direct3D12;
using Vortice.DXGI;
using D11 = Vortice.Direct3D11;
using D9 = Vortice.Direct3D9;

namespace ScottPlot.D3D12;

/// <summary>Shares the D3D11On12/D3D9 presentation devices among controls borrowing the same graphics device.</summary>
internal sealed class WpfInteropDevices : IDisposable
{
    [ThreadStatic] private static Dictionary<D3D12GraphicsDevice, WpfInteropDevices>? Pool;
    private readonly D3D12GraphicsDevice Graphics;
    private int References;
    internal readonly D11.ID3D11Device Device11;
    internal readonly D11.ID3D11DeviceContext Context11;
    internal readonly D3D11On12Interop.On12Device On12;
    internal readonly D9.IDirect3D9Ex D3D9;
    internal readonly D9.IDirect3DDevice9Ex Device9;
    private WpfInteropDevices(D3D12GraphicsDevice graphics)
    {
        Graphics = graphics;
        (Device11, Context11, On12) = D3D11On12Interop.Create(graphics.NativeDevice.NativePointer, graphics.CommandQueue.NativePointer);
        try
        {
            D3D9 = D9.D3D9.Direct3DCreate9Ex();
            try
            {
                uint adapter = 0;
                long luid = graphics.Adapter.Description1.Luid;
                while (adapter < D3D9.AdapterCount && D3D9.GetAdapterLuid(adapter) != luid) adapter++;
                if (adapter == D3D9.AdapterCount) throw new PlatformNotSupportedException("D3DImage requires a D3D9Ex adapter matching the hardware D3D12 adapter.");
                Device9 = D3D9.CreateDeviceEx(adapter, D9.DeviceType.Hardware, nint.Zero,
                    D9.CreateFlags.HardwareVertexProcessing | D9.CreateFlags.Multithreaded | D9.CreateFlags.FpuPreserve,
                    new D9.PresentParameters { Windowed = true, SwapEffect = D9.SwapEffect.Discard, DeviceWindowHandle = GetDesktopWindow() });
            }
            catch { D3D9.Dispose(); throw; }
        }
        catch { On12.Dispose(); Context11.Dispose(); Device11.Dispose(); throw; }
    }
    internal static WpfInteropDevices Acquire(D3D12GraphicsDevice graphics)
    {
        graphics.VerifyAccess();
        Pool ??= new();
        if (!Pool.TryGetValue(graphics, out WpfInteropDevices? devices)) { devices = new(graphics); Pool.Add(graphics, devices); }
        devices.References++; return devices;
    }
    /// <inheritdoc />
    public void Dispose()
    {
        if (--References != 0) return;
        Pool!.Remove(Graphics);
        try { Context11.Flush(); if (!Graphics.IsDeviceLost) Graphics.WaitForIdle(); }
        finally { Device9.Dispose(); D3D9.Dispose(); On12.Dispose(); Context11.Dispose(); Device11.Dispose(); }
    }
    [DllImport("user32.dll")] private static extern nint GetDesktopWindow();
}

/// <summary>Presents only GPU copies of the resolved full Figure through a D3DImage.</summary>
internal sealed class WpfImagePresenter : IDisposable
{
    private readonly D3D12Renderer Renderer;
    private readonly WpfInteropDevices Interop;
    internal readonly D3DImage Image = new();
    private D11.ID3D11Resource? Wrapped;
    private D11.ID3D11Texture2D? Shared11;
    private D9.IDirect3DTexture9? Texture9;
    private D9.IDirect3DSurface9? Surface9;
    private nint BoundResource;
    private bool Disposed;
    internal WpfImagePresenter(D3D12Renderer renderer) { Renderer = renderer; Interop = WpfInteropDevices.Acquire(renderer.Device); }
    internal bool Present(int width, int height)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        if (!Image.IsFrontBufferAvailable) return false;
        Image.Lock();
        try
        {
            if (BoundResource != Renderer.ResolvedTexture.NativePointer) Bind();
            Interop.On12.AcquireWrappedResources(Wrapped!);
            Interop.Context11.CopyResource(Shared11!, Wrapped!);
            Interop.On12.ReleaseWrappedResources(Wrapped!);
            Interop.Context11.Flush();
            Renderer.Device.WaitForIdle();
            Image.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally { Image.Unlock(); }
        return true;
    }
    private void Bind()
    {
        ReleaseBinding();
        Wrapped = Interop.On12.CreateWrappedResource(Renderer.ResolvedTexture, ResourceStates.RenderTarget, ResourceStates.RenderTarget);
        Interop.On12.ReleaseWrappedResources(Wrapped); Interop.Context11.Flush();
        Shared11 = Interop.Device11.CreateTexture2D(new D11.Texture2DDescription
        {
            Width = (uint)Renderer.CapacityWidth, Height = (uint)Renderer.CapacityHeight, MipLevels = 1, ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm, SampleDescription = new SampleDescription(1, 0), Usage = D11.ResourceUsage.Default,
            BindFlags = D11.BindFlags.RenderTarget | D11.BindFlags.ShaderResource, MiscFlags = D11.ResourceOptionFlags.Shared,
        });
        using IDXGIResource resource = Shared11.QueryInterface<IDXGIResource>();
        nint handle = resource.SharedHandle;
        Texture9 = Interop.Device9.CreateTexture((uint)Renderer.CapacityWidth, (uint)Renderer.CapacityHeight, 1, D9.Usage.RenderTarget, D9.Format.A8R8G8B8, D9.Pool.Default, ref handle);
        Surface9 = Texture9.GetSurfaceLevel(0);
        Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, Surface9.NativePointer, enableSoftwareFallback: false);
        BoundResource = Renderer.ResolvedTexture.NativePointer;
    }
    private void ReleaseBinding()
    {
        Image.SetBackBuffer(D3DResourceType.IDirect3DSurface9, nint.Zero);
        Wrapped?.Dispose(); Wrapped = null; Surface9?.Dispose(); Surface9 = null;
        Texture9?.Dispose(); Texture9 = null; Shared11?.Dispose(); Shared11 = null; BoundResource = 0;
    }
    /// <inheritdoc />
    public void Dispose()
    {
        if (Disposed) return;
        try
        {
            if (!Renderer.Device.IsDeviceLost) Renderer.Device.WaitForIdle(); Image.Lock();
            try { ReleaseBinding(); } finally { Image.Unlock(); }
        }
        finally { Disposed = true; Interop.Dispose(); }
    }
}

