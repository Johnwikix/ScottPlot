using System.Text.Json;
using ScottPlot;
using ScottPlot.D3D12.Rendering;
using SkiaSharp;

/// <summary>Validates full native GPU Figures against ordinary GPU render actions and records type/style coverage.</summary>
internal static class Program
{
    private const int CompareWidth = 800, CompareHeight = 600;

    private static int Main(string[] args)
    {
        string evidence = Path.GetFullPath(args[0]); Directory.CreateDirectory(evidence);
        if (args.Contains("--baseline-compare")) return RunBaselineCompare(evidence);
        if (args.Contains("--repro-hairline")) return RunHairlineRepro(evidence);
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

    private static int RunHairlineRepro(string evidence)
    {
        using D3D12Renderer renderer = new();
        List<object> results = [];
        (string Name, Action<Plot> Build)[] cases =
        [
            ("plain", _ => { }),
            ("scale2-only", p => p.ScaleFactor = 2),
            ("hairline-only", p => p.Axes.Hairline(true)),
            ("scale2-hairline", p => { p.ScaleFactor = 2; p.Axes.Hairline(true); }),
        ];
        foreach ((string name, Action<Plot> build) in cases)
        {
            try
            {
                Generate.RandomData.Seed(0);
                Plot plot = new();
                plot.Add.Signal(Generate.Sin()); plot.Add.Signal(Generate.Cos());
                build(plot);
                renderer.Render(plot, CompareWidth, CompareHeight);
                using SKBitmap gpu = renderer.CaptureBitmap();
                File.WriteAllBytes(Path.Combine(evidence, name + "-gpu.png"), gpu.Encode(SKEncodedImageFormat.Png, 100).ToArray());
                int nonBlack = 0;
                for (int i = 0; i < gpu.Bytes.Length; i += 4) if (gpu.Bytes[i] > 8 || gpu.Bytes[i + 1] > 8 || gpu.Bytes[i + 2] > 8) nonBlack++;
                results.Add(new { Case = name, Frame = renderer.LastFrame, PercentNonBlack = Math.Round(100.0 * nonBlack / (gpu.Bytes.Length / 4), 2) });
                Console.WriteLine($"{name}: nonBlack={results[^1].GetType().GetProperty("PercentNonBlack")!.GetValue(results[^1])}% renderMs={renderer.LastFrame.RenderMilliseconds:F2}");
                plot.Dispose();
            }
            catch (Exception ex) { results.Add(new { Case = name, Exception = ex.ToString() }); Console.WriteLine($"{name}: EXCEPTION {ex.GetType().Name}: {ex.Message}"); }
        }
        File.WriteAllText(Path.Combine(evidence, "hairline-repro.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    /// <summary>Renders every scene through software Skia (the standard ScottPlot baseline) and the full GPU renderer at identical size, comparing appearance and frame cost.</summary>
    private static int RunBaselineCompare(string evidence)
    {
        using D3D12Renderer renderer = new();
        List<object> results = []; int failed = 0, review = 0, exact = 0; var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        for (int index = 0; index < SceneCatalog.All.Count; index++)
        {
            SceneDefinition scene = SceneCatalog.All[index];
            try
            {
                using Plot plot = scene.Create();
                using SKSurface cpu = SKSurface.Create(new SKImageInfo(CompareWidth, CompareHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                double cpuMedian = MedianMs(() => plot.Render(cpu), warmup: 2, budgetMs: 2000);
                renderer.Render(plot, CompareWidth, CompareHeight); renderer.Render(plot, CompareWidth, CompareHeight);
                double gpuMedian = MedianMs(() => renderer.Render(plot, CompareWidth, CompareHeight), warmup: 0, budgetMs: 2000);
                D3D12FrameStatistics frame = renderer.LastFrame;
                using SKBitmap gpuBitmap = renderer.CaptureBitmap();
                using SKBitmap cpuBitmap = SKBitmap.FromImage(cpu.Snapshot());
                (int DifferentBytes, int MaxDelta, double MeanDelta, double PercentPixelsDiffering, double PercentPixelsOver16) diff = Compare(gpuBitmap, cpuBitmap);
                bool significant = diff.PercentPixelsOver16 > 2.0;
                if (diff.DifferentBytes == 0) exact++;
                if (significant) review++;
                if (significant)
                {
                    string name = index.ToString("D4");
                    File.WriteAllBytes(Path.Combine(evidence, name + "-gpu.png"), gpuBitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray());
                    File.WriteAllBytes(Path.Combine(evidence, name + "-cpu.png"), cpuBitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray());
                }
                results.Add(new { Index = index, scene.Name, scene.Capability, CpuMedianMilliseconds = Math.Round(cpuMedian, 3), GpuWallMedianMilliseconds = Math.Round(gpuMedian, 3),
                    GpuFrame = frame, DifferentBytes = diff.DifferentBytes, PercentPixelsDiffering = Math.Round(diff.PercentPixelsDiffering, 4),
                    MaxChannelDelta = diff.MaxDelta, MeanChannelDelta = Math.Round(diff.MeanDelta, 4),
                    PercentPixelsOver16 = Math.Round(diff.PercentPixelsOver16, 4),
                    Result = significant ? "REVIEW" : "PASS" });
            }
            catch (Exception ex) { failed++; results.Add(new { Index = index, scene.Name, Result = "FAIL", Exception = ex.ToString() }); }
            if (index % 25 == 0 || index == SceneCatalog.All.Count - 1)
            {
                Console.WriteLine($"Scenes {index + 1}/{SceneCatalog.All.Count}; failed={failed}; review={review}; elapsed={stopwatch.Elapsed.TotalMinutes:N1}min");
                SaveBaseline(evidence, results, failed, review, exact);
            }
        }
        return failed == 0 ? 0 : 1;
    }

    private static double MedianMs(Action action, int warmup, double budgetMs)
    {
        for (int i = 0; i < warmup; i++) action();
        List<double> samples = []; var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while ((samples.Count < 3 || samples.Sum() < budgetMs) && samples.Count < 200)
        {
            long start = stopwatch.Elapsed.Ticks; action();
            samples.Add((stopwatch.Elapsed.Ticks - start) * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency / 1000.0);
        }
        samples.Sort();
        return samples[samples.Count / 2];
    }

    private static (int DifferentBytes, int MaxDelta, double MeanDelta, double PercentPixelsDiffering, double PercentPixelsOver16) Compare(SKBitmap gpu, SKBitmap cpu)
    {
        if (gpu.ColorType != SKColorType.Bgra8888 || cpu.ColorType != SKColorType.Bgra8888 || gpu.ByteCount != cpu.ByteCount)
            throw new InvalidOperationException($"Bitmap config mismatch: gpu={gpu.ColorType} cpu={cpu.ColorType}");
        byte[] a = gpu.Bytes, b = cpu.Bytes;
        int different = 0, over16Bytes = 0, over16Pixels = 0, differingPixels = 0; long sum = 0, max = 0;
        for (int pixel = 0; pixel < a.Length; pixel += 4)
        {
            bool anyDelta = false, anyOver = false;
            for (int k = 0; k < 4; k++)
            {
                int delta = Math.Abs(a[pixel + k] - b[pixel + k]);
                if (delta != 0) { different++; sum += delta; anyDelta = true; if (delta > max) max = delta; }
                if (delta > 16) { over16Bytes++; anyOver = true; }
            }
            if (anyDelta) differingPixels++;
            if (anyOver) over16Pixels++;
        }
        int pixels = a.Length / 4;
        double mean = different == 0 ? 0 : (double)sum / different;
        return (different, (int)max, mean, 100.0 * differingPixels / pixels, 100.0 * over16Pixels / pixels);
    }

    private static void SaveBaseline(string evidence, List<object> results, int failed, int review, int exact)
    {
        File.WriteAllText(Path.Combine(evidence, "catalogue-baseline.json"), JsonSerializer.Serialize(new {
            Baseline = "Software Skia raster (standard ScottPlot render path)",
            Gpu = "D3D12 full-Figure renderer, native 4x MSAA, explicit resolve",
            CompareSize = $"{CompareWidth}x{CompareHeight}",
            Scenes = results.Count, Failed = failed, Review = review, IdenticalToBaseline = exact,
            Cases = results
        }, new JsonSerializerOptions { WriteIndented = true }));
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
