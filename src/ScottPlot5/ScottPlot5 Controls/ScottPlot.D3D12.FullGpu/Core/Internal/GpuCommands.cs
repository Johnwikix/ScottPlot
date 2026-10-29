using System.Buffers;
using Vortice.Direct3D12;

namespace ScottPlot.D3D12.Rendering.Internal;

/// <summary>Submits fixed native command records through reusable pooled marshaling arrays.</summary>
internal static class GpuCommands
{
    internal static void Transition(ID3D12GraphicsCommandList list, ID3D12Resource resource, ResourceStates before, ResourceStates after)
    {
        // 池化固定大小命令缓冲，预热后不为原生互操作创建临时数组；兼容 .NET 8/10。
        ResourceBarrier[] records = ArrayPool<ResourceBarrier>.Shared.Rent(1);
        try { records[0] = ResourceBarrier.BarrierTransition(resource, before, after); list.ResourceBarrier(1, records); }
        finally { ArrayPool<ResourceBarrier>.Shared.Return(records); }
    }
    internal static void Execute(ID3D12CommandQueue queue, ID3D12GraphicsCommandList list)
    {
        ID3D12CommandList[] commands = ArrayPool<ID3D12CommandList>.Shared.Rent(1);
        try { commands[0] = list; queue.ExecuteCommandLists(1, commands); }
        finally { ArrayPool<ID3D12CommandList>.Shared.Return(commands, clearArray: true); }
    }
}
