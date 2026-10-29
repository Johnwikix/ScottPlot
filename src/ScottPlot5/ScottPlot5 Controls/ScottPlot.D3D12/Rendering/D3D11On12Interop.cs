using SharpGen.Runtime;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.Direct3D12;

namespace ScottPlot.D3D12.Rendering;

internal static class D3D11On12Interop
{
    private static readonly Guid IidID3D11On12Device = new("85611e73-70a9-490e-9614-a9e302777904");
    private static readonly Guid IidIUnknown = new("00000000-0000-0000-c000-000000000046");
    private const uint DeviceFlagsBgraSupport = 0x20;

    [DllImport("d3d11.dll")]
    private static extern int D3D11On12CreateDevice(
        nint device12,
        uint flags,
        nint featureLevels,
        uint featureLevelCount,
        nint[] commandQueues,
        uint commandQueueCount,
        uint nodeMask,
        out nint device11,
        out nint context11,
        nint chosenFeatureLevel);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateWrappedResourceDelegate(
        nint thisPtr,
        nint resource12,
        in D3D11On12ResourceFlags flags,
        ResourceStates inState,
        ResourceStates outState,
        in Guid riid,
        out nint resource11);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void WrappedResourcesDelegate(nint thisPtr, nint[] resources, uint count);

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D11On12ResourceFlags
    {
        public uint BindFlags;
        public uint MiscFlags;
        public uint CPUAccessFlags;
        public uint StructureByteStride;
    }

    public static (ID3D11Device Device11, ID3D11DeviceContext Context11, On12Device On12) Create(nint device12, nint queue)
    {
        int hr = D3D11On12CreateDevice(device12, DeviceFlagsBgraSupport, nint.Zero, 0, [queue], 1, 0, out nint device11, out nint context11, nint.Zero);
        if (hr < 0)
            throw new InvalidOperationException($"D3D11On12CreateDevice failed: 0x{hr:X8}");
        if (device11 == 0 || context11 == 0)
            throw new InvalidOperationException("D3D11On12CreateDevice returned null pointers.");

        int hrOn12 = Marshal.QueryInterface(device11, in IidID3D11On12Device, out nint on12);
        if (hrOn12 < 0 || on12 == 0)
            throw new InvalidOperationException($"ID3D11On12Device query failed: 0x{hrOn12:X8}");

        return (new ID3D11Device(device11), new ID3D11DeviceContext(context11), new On12Device(on12));
    }

    internal sealed class On12Device : IDisposable
    {
        private readonly nint _pointer;
        private readonly CreateWrappedResourceDelegate _createWrappedResource;
        private readonly WrappedResourcesDelegate _releaseWrappedResources;
        private readonly WrappedResourcesDelegate _acquireWrappedResources;

        public On12Device(nint pointer)
        {
            _pointer = pointer;
            nint vtable = Marshal.ReadIntPtr(pointer);
            _createWrappedResource = Marshal.GetDelegateForFunctionPointer<CreateWrappedResourceDelegate>(Marshal.ReadIntPtr(vtable, 3 * nint.Size));
            _releaseWrappedResources = Marshal.GetDelegateForFunctionPointer<WrappedResourcesDelegate>(Marshal.ReadIntPtr(vtable, 4 * nint.Size));
            _acquireWrappedResources = Marshal.GetDelegateForFunctionPointer<WrappedResourcesDelegate>(Marshal.ReadIntPtr(vtable, 5 * nint.Size));
        }

        public ID3D11Resource CreateWrappedResource(ID3D12Resource resource12, ResourceStates inState, ResourceStates outState)
        {
            D3D11On12ResourceFlags flags = new();
            int hr = _createWrappedResource(_pointer, resource12.NativePointer, in flags, inState, outState, in IidIUnknown, out nint resource11);
            if (hr < 0)
                throw new InvalidOperationException($"CreateWrappedResource failed: 0x{hr:X8}");
            if (resource11 == 0)
                throw new InvalidOperationException("CreateWrappedResource returned null.");

            using ComObject unknown = new(resource11);
            return unknown.QueryInterface<ID3D11Resource>();
        }

        public void AcquireWrappedResources(ID3D11Resource wrappedResource11) => _acquireWrappedResources(_pointer, [wrappedResource11.NativePointer], 1);
        public void ReleaseWrappedResources(ID3D11Resource wrappedResource11) => _releaseWrappedResources(_pointer, [wrappedResource11.NativePointer], 1);
        public void Dispose() => Marshal.Release(_pointer);
    }
}
