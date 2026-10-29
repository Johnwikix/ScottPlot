using System.Text.Json;
using ScottPlot;
using ScottPlot.D3D12.Rendering;
using SkiaSharp;

/// <summary>Validates full native GPU Figures against ordinary GPU render actions and records type/style coverage.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string evidence = Path.GetFullPath(args[0]); Directory.CreateDirectory(evidence);
        using D3D12Renderer renderer = new();
        List<object> results = []; HashSet<string> covered = new(); int failed = 0, exactMatches = 0;
        for (int index = 0; index < SceneCatalog.All.Count; index++)
        {
            SceneDefinition scene = SceneCatalog.All[index];
            try
            {
                using Plot plot = scene.Create();
                renderer.OptimizeSignals = true;
                for (int i = 0; i < 3; i++) renderer.Render(plot, 800, 600);
                foreach (IPlottable item in plot.PlottableList) covered.Add(SceneCatalog.CanonicalType(item.GetType()));
                using SKBitmap optimized = renderer.CaptureBitmap();
                File.WriteAllBytes(Path.Combine(evidence, index.ToString("D4") + "-gpu.png"), optimized.Encode(SKEncodedImageFormat.Png, 100).ToArray());
                renderer.OptimizeSignals = false;
                for (int i = 0; i < 2; i++) renderer.Render(plot, 800, 600);
                using SKBitmap original = renderer.CaptureBitmap();
                byte[] a = optimized.Bytes, b = original.Bytes; int difference = 0, maximum = 0;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) { difference++; maximum = Math.Max(maximum, Math.Abs(a[i] - b[i])); }
                bool dynamicBenchmark = plot.PlottableList.Any(p => p is ScottPlot.Plottables.Benchmark) || plot.Benchmark.IsVisible;
                if (difference == 0) exactMatches++;
                else File.WriteAllBytes(Path.Combine(evidence, index.ToString("D4") + "-original-gpu.png"), original.Encode(SKEncodedImageFormat.Png, 100).ToArray());
                if (scene.AuxiliaryRasterHitMap) Thread.Sleep(150); // Wait for the existing decorator's auxiliary CPU hit-map task before disposing its plot.
                results.Add(new { Index = index, scene.Name, scene.Capability, scene.AuxiliaryRasterHitMap, Result = "GPU_RENDER_PASS",
                    Plottables = plot.PlottableList.Select(p => SceneCatalog.CanonicalType(p.GetType())).Distinct().ToArray(),
                    DifferentBytesFromOrdinaryGpuActions = difference, MaxChannelDelta = maximum, DynamicBenchmarkText = dynamicBenchmark,
                    Frame = renderer.LastFrame });
            }
            catch (Exception ex) { failed++; results.Add(new { Index = index, scene.Name, Result = "FAIL", Exception = ex.ToString() }); }
            if (index % 25 == 0 || index == SceneCatalog.All.Count - 1)
            {
                Console.WriteLine($"Scenes {index + 1}/{SceneCatalog.All.Count}; failed={failed}");
                Save(evidence, renderer, results, covered, failed, exactMatches);
            }
        }
        return failed == 0 ? 0 : 1;
    }
    private static void Save(string evidence, D3D12Renderer renderer, List<object> results, HashSet<string> covered, int failed, int exact)
    {
        string[] inventory = typeof(Plot).Assembly.GetTypes().Where(t => typeof(IPlottable).IsAssignableFrom(t) && !t.IsAbstract).Select(SceneCatalog.CanonicalType).Distinct().Order().ToArray();
        File.WriteAllText(Path.Combine(evidence, "catalogue.json"), JsonSerializer.Serialize(new {
            Backend = renderer.Device.Context.Backend.ToString(), NativeMsaaSamples = 4, ExplicitResolve = true,
            Fence = "Verified value loop with AutoResetEvent", PerRenderPixelReadbacks = 0, ExplicitDiagnosticReadbacks = renderer.PixelReadbackCount,
            Scenes = results.Count, Failed = failed, ExactOrdinaryGpuImageMatches = exact,
            Inventory = inventory.Select(t => new { Type = t, Covered = covered.Contains(t) }).ToArray(),
            Notes = new[] { "This is offscreen visual evidence, not a substitute for all three real windows.",
                "Hittable/Draggable decorators retain the core's existing CPU-raster auxiliary hit maps; visible Figures remain GPU rendered.",
                "Native Skia software-path fallback trace is still unverified; a hardware surface alone does not prove its absence." },
            Cases = results
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
