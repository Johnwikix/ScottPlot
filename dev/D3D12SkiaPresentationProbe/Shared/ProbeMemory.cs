using System.Diagnostics;
using SkiaSharp;
using Vortice.DXGI;

/// <summary>Samples separate managed, process and DXGI memory boundaries outside the render timing path.</summary>
internal sealed class ProbeMemory : IDisposable
{
    private readonly Process Process = Process.GetCurrentProcess();
    private readonly IDXGIAdapter3 Adapter;
    private readonly GRContext Context;
    internal ProbeMemory(IDXGIAdapter1 adapter, GRContext context) { Adapter = adapter.QueryInterface<IDXGIAdapter3>(); Context = context; }
    internal object Sample(string stage, GpuFigureSurface surface, int width, int height)
    {
        Process.Refresh();
        Context.GetResourceCacheUsage(out int resources, out long bytes);
        var local = Adapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
        return new { stage, Process.Id, Process.PrivateMemorySize64, Process.WorkingSet64, Process.HandleCount,
            ManagedLiveEstimateBytes = GC.GetTotalMemory(false), ManagedCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
            Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2),
            SkiaCacheBytes = bytes, SkiaCacheResources = resources, LocalGpuUsageBytes = local.CurrentUsage,
            LocalGpuBudgetBytes = local.Budget, ViewportWidth = width, ViewportHeight = height,
            CapacityWidth = surface.Width, CapacityHeight = surface.Height, TargetAllocations = surface.Allocations };
    }
    /// <inheritdoc />
    public void Dispose() { Adapter.Dispose(); Process.Dispose(); }
}
