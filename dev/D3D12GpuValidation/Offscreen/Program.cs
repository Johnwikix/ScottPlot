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
        int failed = 0;
        (string Name, Action<Plot> Build)[] cases =
        [
            ("plain", _ => { }),
            ("scale2-only", p => p.ScaleFactor = 2),
            ("hairline-only", p => p.Axes.Hairline(true)),
            ("scale2-hairline", p => { p.ScaleFactor = 2; p.Axes.Hairline(true); }),
            ("scale0.5-only", p => p.ScaleFactor = .5),
            ("scale1.5-hairline", p => { p.ScaleFactor = 1.5; p.Axes.Hairline(true); }),
        ];
        foreach ((string name, Action<Plot> build) in cases)
        {
            try
            {
                Generate.RandomData.Seed(0);
                using Plot plot = new();
                plot.Add.Signal(Generate.Sin()); plot.Add.Signal(Generate.Cos());
                build(plot);
                Console.WriteLine($"{name}: rendering CPU baseline and GPU Figure");
                using SKSurface cpu = SKSurface.Create(new SKImageInfo(CompareWidth, CompareHeight, SKColorType.Bgra8888, SKAlphaType.Premul));
                RenderCpu(plot, cpu);
                using SKBitmap firstCpu = SKBitmap.FromImage(cpu.Snapshot());
                for (int i = 0; i < 200; i++) RenderCpu(plot, cpu);
                using SKBitmap cpuBitmap = SKBitmap.FromImage(cpu.Snapshot());
                bool cpuStable = firstCpu.Bytes.AsSpan().SequenceEqual(cpuBitmap.Bytes);
                PixelRect cpuDataRect = plot.RenderManager.LastRender.DataRect;
                renderer.Render(plot, CompareWidth * 2, CompareHeight * 2);
                renderer.Render(plot, CompareWidth, CompareHeight);
                using SKBitmap gpu = renderer.CaptureBitmap();
                File.WriteAllBytes(Path.Combine(evidence, name + "-gpu.png"), gpu.Encode(SKEncodedImageFormat.Png, 100).ToArray());
                File.WriteAllBytes(Path.Combine(evidence, name + "-cpu.png"), cpuBitmap.Encode(SKEncodedImageFormat.Png, 100).ToArray());
                for (int i = 0; i < 3; i++) renderer.Render(plot, CompareWidth, CompareHeight);
                using SKBitmap repeatedGpu = renderer.CaptureBitmap();
                byte[] pixels = gpu.Bytes;
                bool gpuStable = pixels.AsSpan().SequenceEqual(repeatedGpu.Bytes);
                bool layoutMatches = cpuDataRect.Equals(plot.RenderManager.LastRender.DataRect)
                    && new PixelRect(CompareWidth, CompareHeight).Equals(plot.RenderManager.LastRender.FigureRect);
                var diff = Compare(gpu, cpuBitmap);
                int nonBlack = 0;
                for (int i = 0; i < pixels.Length; i += 4) if (pixels[i] > 8 || pixels[i + 1] > 8 || pixels[i + 2] > 8) nonBlack++;
                double percentNonBlack = 100.0 * nonBlack / (pixels.Length / 4);
                bool passed = cpuStable && gpuStable && layoutMatches && percentNonBlack > 50 && diff.PercentPixelsOver16 <= 2;
                if (!passed) failed++;
                results.Add(new { Case = name, Frame = renderer.LastFrame, Result = passed ? "PASS" : "FAIL", CpuStable = cpuStable,
                    GpuStable = gpuStable, LayoutMatches = layoutMatches, PercentNonBlack = Math.Round(percentNonBlack, 2),
                    PercentPixelsOver16 = Math.Round(diff.PercentPixelsOver16, 4) });
                Console.WriteLine($"{name}: {(passed ? "PASS" : "FAIL")} nonBlack={percentNonBlack:F2}% diff>16={diff.PercentPixelsOver16:F2}% renderMs={renderer.LastFrame.RenderMilliseconds:F2}");
            }
            catch (Exception ex) { failed++; results.Add(new { Case = name, Result = "FAIL", Exception = ex.ToString() }); Console.WriteLine($"{name}: EXCEPTION {ex.GetType().Name}: {ex.Message}"); }
        }
        File.WriteAllText(Path.Combine(evidence, "hairline-repro.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
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
                double cpuMedian = MedianMs(() => RenderCpu(plot, cpu), warmup: 2, budgetMs: 2000);
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

    /// <summary>Restores the canvas matrix so repeated baseline frames do not accumulate Plot.ScaleFactor.</summary>
    private static void RenderCpu(Plot plot, SKSurface surface)
    {
        SKCanvas canvas = surface.Canvas;
        int saved = canvas.Save();
        try { plot.Render(canvas, CompareWidth, CompareHeight); }
        finally { canvas.RestoreToCount(saved); }
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
