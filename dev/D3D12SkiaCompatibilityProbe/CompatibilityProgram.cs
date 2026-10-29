using System.Text.Json;
using ScottPlot;
using ScottPlotCookbook;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

/// <summary>Smoke-renders existing Cookbook recipes on a verified GPU surface and records exact type coverage.</summary>
/// <remarks>Submission success does not establish pixel equivalence, complete style coverage, or absence of auxiliary CPU work in core plottables.</remarks>
internal static class CompatibilityProgram
{
    private static int Main(string[] args)
    {
        string evidence = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(evidence);
        using IDXGIFactory4 factory = CreateDXGIFactory2<IDXGIFactory4>(false);
        factory.EnumAdapters1(0, out IDXGIAdapter1 adapter).CheckError();
        using (adapter)
        {
            if ((adapter.Description1.Flags & AdapterFlags.Software) != 0) throw new InvalidOperationException("Software adapter forbidden.");
            using ID3D12Device device = D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0);
            using ID3D12CommandQueue queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
            using ID3D12Fence fence = device.CreateFence(0);
            using ManualResetEvent wait = new(false);
            using GRD3DBackendContext backend = new() { Adapter = adapter.NativePointer, Device = device.NativePointer, Queue = queue.NativePointer };
            using GRContext context = GRContext.CreateDirect3D(backend) ?? throw new InvalidOperationException("No D3D12 context.");
            using ID3D12Resource target = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
                ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, 800, 600, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
            using GRD3DTextureResourceInfo info = new() { Resource = target.NativePointer, ResourceState = (uint)ResourceStates.RenderTarget,
                Format = (uint)Format.B8G8R8A8_UNorm, SampleCount = 1, LevelCount = 1 };
            using GRBackendTexture texture = new(800, 600, info);
            using SKSurface surface = SKSurface.Create(context, texture, GRSurfaceOrigin.TopLeft, 0, SKColorType.Bgra8888)
                ?? throw new InvalidOperationException("Cannot create GPU surface.");
            if (context.Backend != GRBackend.Direct3D || surface.Context?.Backend != GRBackend.Direct3D || surface.PeekPixels() is not null)
                throw new InvalidOperationException("GPU verification failed.");

            List<CaseResult> cases = new();
            ulong sequence = 0;
            Type[] recipes = typeof(IRecipe).Assembly.GetTypes().Where(t => typeof(IRecipe).IsAssignableFrom(t)
                && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null).OrderBy(t => t.FullName).ToArray();
            foreach (Type type in recipes)
            {
                using Plot plot = new();
                string[] types = [];
                string? failure = null;
                try
                {
                    Generate.RandomData.Seed(0);
                    Fonts.Reset();
                    IRecipe recipe = (IRecipe)Activator.CreateInstance(type)!;
                    recipe.Execute(plot);
                    types = plot.PlottableList.Select(x => x.GetType().FullName!).Distinct().Order().ToArray();
                    int save = surface.Canvas.Save();
                    try { plot.Render(surface); }
                    finally { surface.Canvas.RestoreToCount(save); }
                    context.Flush(true, false);
                    queue.Signal(fence, ++sequence).CheckError();
                    if (fence.CompletedValue < sequence)
                    {
                        fence.SetEventOnCompletion(sequence, wait.SafeWaitHandle.DangerousGetHandle()).CheckError();
                        if (!wait.WaitOne(TimeSpan.FromSeconds(20))) throw new TimeoutException("GPU fence timeout.");
                    }
                    if (context.IsAbandoned || surface.Context?.Backend != GRBackend.Direct3D)
                        throw new InvalidOperationException("GPU context lost.");
                    device.DeviceRemovedReason.CheckError();
                    if (surface.Canvas.TotalMatrix != SKMatrix.Identity)
                        throw new InvalidOperationException("Canvas matrix not restored.");
                }
                catch (Exception ex) { failure = ex.ToString(); }
                cases.Add(new(type.FullName!, types, failure is null ? "GPU_SUBMISSION_PASS" : "FAIL", failure));
                if (cases.Count % 50 == 0) System.Console.WriteLine($"Recipes {cases.Count}/{recipes.Length}; failures {cases.Count(x => x.Error is not null)}");
            }
            string[] covered = cases.Where(x => x.Error is null).SelectMany(x => x.Plottables).Distinct().Order().ToArray();
            var inventory = typeof(Plot).Assembly.GetTypes().Where(t => typeof(IPlottable).IsAssignableFrom(t) && !t.IsAbstract)
                .OrderBy(t => t.FullName).Select(t => new { Type = t.FullName,
                    Status = covered.Contains(t.FullName!) ? "Cookbook GPU submission smoke only; visual equivalence UNVERIFIED" : "UNVERIFIED / not exercised by recipe probe" }).ToArray();
            File.WriteAllText(Path.Combine(evidence, "compatibility.json"), JsonSerializer.Serialize(new
            {
                Backend = context.Backend.ToString(), Adapter = adapter.Description1.Description,
                Recipes = cases.Count, Passed = cases.Count(x => x.Error is null), Failed = cases.Count(x => x.Error is not null),
                DistinctTypesExercised = covered.Length, Inventory = inventory, Cases = cases,
                PixelReadbacksInProbe = 0, RasterSurfaceFallbackInProbe = false,
                Scope = "Existing single-Plot IRecipe.Execute + ordinary Plot.Render; no NUnit teardown, no reference-image or per-platform recipe comparison.",
                Unverified = new[] { "pixel equivalence", "exhaustive combinations of styles", "MultiplotRecipeBase", "auxiliary CPU/raster work performed inside recipes/core", "all types on each UI platform" },
            }, new JsonSerializerOptions { WriteIndented = true }));
            return cases.Any(x => x.Error is not null) ? 1 : 0;
        }
    }

    /// <summary>Records a recipe result without reducing rendering exceptions to software fallback.</summary>
    private sealed record CaseResult(string Recipe, string[] Plottables, string Status, string? Error);
}

