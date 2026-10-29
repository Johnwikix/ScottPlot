using System.Diagnostics;
using System.Text.Json;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

/// <summary>Separates Skia resize cache retention from platform presentation memory using repeated identical size cycles.</summary>
/// <remarks>The bounded-cache variant is a diagnostic intervention, not a proven fix for WPF, WinUI, or UWP.</remarks>
internal static class MemoryProgram
{
    private static int Main(string[] args)
    {
        string evidence = Path.GetFullPath(args[0]);
        bool bounded = args.Skip(1).Contains("bounded");
        using IDXGIFactory4 factory = CreateDXGIFactory2<IDXGIFactory4>(false);
        factory.EnumAdapters1(0, out IDXGIAdapter1 adapter).CheckError();
        using (adapter)
        using (IDXGIAdapter3 memoryAdapter = adapter.QueryInterface<IDXGIAdapter3>())
        using (ID3D12Device device = D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0))
        using (ID3D12CommandQueue queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct)))
        using (ID3D12Fence fence = device.CreateFence(0))
        using (ManualResetEvent wait = new(false))
        using (GRD3DBackendContext backend = new() { Adapter = adapter.NativePointer, Device = device.NativePointer, Queue = queue.NativePointer })
        using (GRContext context = GRContext.CreateDirect3D(backend) ?? throw new InvalidOperationException("No D3D12 context."))
        using (ScottPlot.Plot plot = Program.CreatePlot())
        using (Process process = Process.GetCurrentProcess())
        {
            if ((adapter.Description1.Flags & AdapterFlags.Software) != 0 || context.Backend != GRBackend.Direct3D)
                throw new InvalidOperationException("Hardware D3D12 required.");
            long defaultLimit = context.GetResourceCacheLimit();
            if (bounded) context.SetResourceCacheLimit(32L * 1024 * 1024);
            List<object> samples = new();
            ulong sequence = 0;
            void Wait()
            {
                queue.Signal(fence, ++sequence).CheckError();
                if (fence.CompletedValue < sequence)
                {
                    fence.SetEventOnCompletion(sequence, wait.SafeWaitHandle.DangerousGetHandle()).CheckError();
                    if (!wait.WaitOne(TimeSpan.FromSeconds(20))) throw new TimeoutException("GPU fence timeout.");
                }
                device.DeviceRemovedReason.CheckError();
            }
            void Frame(int width, int height)
            {
                using ID3D12Resource target = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
                    ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
                using GRD3DTextureResourceInfo info = new() { Resource = target.NativePointer, ResourceState = (uint)ResourceStates.RenderTarget,
                    Format = (uint)Format.B8G8R8A8_UNorm, SampleCount = 1, LevelCount = 1 };
                using GRBackendTexture texture = new(width, height, info);
                using SKSurface surface = SKSurface.Create(context, texture, GRSurfaceOrigin.TopLeft, 0, SKColorType.Bgra8888)
                    ?? throw new InvalidOperationException("Cannot wrap GPU surface.");
                if (surface.Context?.Backend != GRBackend.Direct3D || surface.PeekPixels() is not null)
                    throw new InvalidOperationException("GPU surface required.");
                int save = surface.Canvas.Save();
                try { plot.Render(surface); }
                finally { surface.Canvas.RestoreToCount(save); }
                context.Flush(true, false);
                Wait();
            }
            void Sample(string stage, int cycle)
            {
                process.Refresh();
                context.GetResourceCacheUsage(out int resources, out long cacheBytes);
                var local = memoryAdapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.Local);
                var nonlocal = memoryAdapter.QueryVideoMemoryInfo(0, MemorySegmentGroup.NonLocal);
                samples.Add(new { stage, cycle, Frames = sequence, process.PrivateMemorySize64, process.WorkingSet64,
                    process.HandleCount, ManagedLiveEstimateBytes = GC.GetTotalMemory(false),
                    ManagedCommittedBytes = GC.GetGCMemoryInfo().TotalCommittedBytes,
                    Gen0 = GC.CollectionCount(0), Gen1 = GC.CollectionCount(1), Gen2 = GC.CollectionCount(2),
                    SkiaCacheBytes = cacheBytes, SkiaCacheResources = resources,
                    LocalGpuUsageBytes = local.CurrentUsage, LocalGpuBudgetBytes = local.Budget,
                    NonLocalGpuUsageBytes = nonlocal.CurrentUsage });
            }
            for (int i = 0; i < 3; i++) Frame(800, 600);
            Sample("warmup", 0);
            for (int cycle = 1; cycle <= 12; cycle++)
            {
                for (int i = 0; i < 24; i++)
                {
                    Frame(640 + i * 27, 480 + i * 17);
                    // Only the candidate deliberately trims unlocked scratch resources after retirement.
                    if (bounded) context.PurgeUnlockedResources(false);
                }
                Sample("resize-cycle", cycle);
            }
            for (int i = 0; i < 30; i++) Frame(800, 600);
            Sample("returned-to-800x600", 12);
            context.PurgeResources();
            Wait();
            Sample("explicit-diagnostic-purge", 12);
            File.WriteAllText(Path.Combine(evidence, bounded ? "memory-resize-bounded.json" : "memory-resize-default.json"),
                JsonSerializer.Serialize(new { Backend = context.Backend.ToString(), Adapter = adapter.Description1.Description,
                    DefaultSkiaCacheLimitBytes = defaultLimit, ConfiguredSkiaCacheLimitBytes = context.GetResourceCacheLimit(),
                    RepeatedSizeCycles = 12, UniqueSizes = 24, Sizes = "640+27*i by 480+17*i; i=0..23",
                    PerFramePixelReadbacks = 0, ForcedGc = false, BoundedCacheIntervention = bounded,
                    Scope = "Offscreen full Figure only. Excludes D3DImage/D3D11On12/D3D9, XAML, DXGI swapchain and compositor.",
                    Samples = samples }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }
}
