using System.Diagnostics;
using System.Runtime;
using System.Runtime.Intrinsics;
using System.Text.Json;

/// <summary>Measures complete GPU Figures, including GPU completion, after exact sampling and visual checks.</summary>
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly Variant[] Variants = [
        new("original-msaa4", false, false, false, false),
        new("reused-scalar", true, false, false, false),
        new("shared-x-scalar", true, true, false, false),
        new("shared-x-simd", true, true, true, false),
        new("shared-x-simd-bulkpath", true, true, true, true),
        new("bulkpath-zero-marker", true, true, true, true, true)
    ];
    private static int Main(string[] args)
    {
        string evidence = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(evidence);
        try
        {
            using GpuHarness gpu = new();
            using GpuTarget target = new(gpu);
            if (args.Contains("--marker-only")) { Save(evidence, "marker-guards.json", MarkerChecks.Run(gpu, target, evidence)); return 0; }
            if (args.Contains("--stage-only")) { Stages(gpu, target, evidence); return 0; }
            Console.WriteLine("Checking sampling/path semantics.");
            object semantics = SemanticChecks.Run(gpu, target);
            Save(evidence, "semantics.json", semantics);
            if (args.Contains("--semantic-only")) return 0;
            List<object> visual = [];
            byte[]? reference = null;
            foreach (Variant variant in Variants)
            {
                using FigureFixture figure = new(variant);
                for (int i = 0; i < 6; i++) { target.Render(figure.Plot); gpu.Context.Flush(true, false); gpu.Wait(); }
                byte[] pixels = target.DiagnosticPixels(Path.Combine(evidence, variant.Name + ".png"));
                reference ??= pixels;
                int differentBytes = 0, maxDelta = 0;
                for (int i = 0; i < pixels.Length; i++)
                    if (pixels[i] != reference[i]) { differentBytes++; maxDelta = Math.Max(maxDelta, Math.Abs(pixels[i] - reference[i])); }
                visual.Add(new { variant.Name, DifferentBytes = differentBytes, MaxChannelDelta = maxDelta });
                if (differentBytes != 0) throw new InvalidOperationException($"{variant.Name}: full Figure pixels differ: {differentBytes}, delta {maxDelta}.");
            }
            Save(evidence, "visual-comparison.json", visual);
            Console.WriteLine("Full Figure pixel equality passed.");
            List<Case> cases = [];
            List<object> memory = [];
            memory.Add(gpu.Memory("before-measurement"));
            for (int round = 0; round < 3; round++)
            foreach (string scenario in new[] { "full-redraw", "all-y-updated-in-place" })
            foreach (Variant variant in round % 2 == 0 ? Variants : Variants.Reverse())
            {
                Console.WriteLine($"round {round + 1} / {scenario} / {variant.Name}");
                using FigureFixture figure = new(variant);
                List<Frame> frames = new(120);
                for (int i = -30; i < 120; i++)
                {
                    long updateStart = Stopwatch.GetTimestamp();
                    if (scenario == "all-y-updated-in-place") figure.UpdateData(i + 30);
                    double updateMs = Ms(Stopwatch.GetTimestamp() - updateStart);
                    figure.ClearCounters();
                    long allocated = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    target.Render(figure.Plot);
                    long rendered = Stopwatch.GetTimestamp();
                    gpu.Context.Flush(true, false);
                    long submitted = Stopwatch.GetTimestamp();
                    gpu.Wait();
                    long completed = Stopwatch.GetTimestamp();
                    long bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
                    if (i >= 0) frames.Add(new(i, Ms(completed - start), Ms(rendered - start), Ms(submitted - rendered),
                        Ms(completed - submitted), bytes, updateMs, Ms(figure.Sources.Sum(s => s.DataTicks)),
                        Ms(figure.Paths.Sum(p => p.PathTicks)), figure.Sources.Sum(s => s.Pixels.Count),
                        figure.Sources.Sum(s => s.VectorRanges), figure.Sources.Sum(s => s.SpecialScalarRanges)));
                }
                cases.Add(new(round + 1, scenario, variant.Name, 30, frames));
                memory.Add(gpu.Memory($"round-{round + 1}/{scenario}/{variant.Name}"));
                Save(evidence, "frames.json", cases);
            }
            // Same optimized fixture at a fixed size, then repeated resize sizes. No forced GC during measurements.
            using (FigureFixture figure = new(Variants[^1]))
            {
                for (int i = 0; i < 1200; i++)
                {
                    figure.UpdateData(i); target.Render(figure.Plot); gpu.Context.Flush(true, false); gpu.Wait();
                    if (i % 200 == 199) memory.Add(gpu.Memory("steady-" + (i + 1)));
                }
                for (int cycle = 1; cycle <= 12; cycle++)
                {
                    for (int i = 0; i < 24; i++)
                    {
                        using GpuTarget resize = new(gpu, 640 + i * 27, 480 + i * 17);
                        target.Surface.Canvas.ResetMatrix();
                        resize.Render(figure.Plot); gpu.Context.Flush(true, false); gpu.Wait();
                    }
                    memory.Add(gpu.Memory("resize-cycle-" + cycle));
                }
            }
            gpu.Context.PurgeResources(); gpu.Wait();
            memory.Add(gpu.Memory("after-explicit-skia-purge"));
            Save(evidence, "memory.json", memory);
            var summary = cases.GroupBy(c => new { c.Scenario, c.Variant }).Select(group =>
            {
                Frame[] frames = group.SelectMany(c => c.Frames).ToArray();
                return new { group.Key.Scenario, group.Key.Variant, Count = frames.Length,
                    MedianMs = Percentile(frames.Select(f => f.TotalMs), .5), P95Ms = Percentile(frames.Select(f => f.TotalMs), .95),
                    P99Ms = Percentile(frames.Select(f => f.TotalMs), .99), MaxMs = frames.Max(f => f.TotalMs),
                    MedianPlotMs = Percentile(frames.Select(f => f.PlotMs), .5), MedianFlushMs = Percentile(frames.Select(f => f.FlushMs), .5),
                    MedianFenceMs = Percentile(frames.Select(f => f.FenceMs), .5), MedianAllocatedBytes = Percentile(frames.Select(f => (double)f.AllocatedBytes), .5),
                    MedianDataMs = Percentile(frames.Select(f => f.DataMs), .5), MedianBulkPathMs = Percentile(frames.Select(f => f.BulkPathMs), .5),
                    MedianUpdateOutsideRenderMs = Percentile(frames.Select(f => f.UpdateOutsideRenderMs), .5),
                    AllRounds = group.Select(c => new { c.Round, MedianMs = Percentile(c.Frames.Select(f => f.TotalMs), .5),
                        P95Ms = Percentile(c.Frames.Select(f => f.TotalMs), .95) }).ToArray() };
            }).ToArray();
            Save(evidence, "summary.json", new { Backend = gpu.Context.Backend.ToString(), Adapter = gpu.Adapter.Description1.Description,
                Runtime = Environment.Version.ToString(), Environment.ProcessorCount, Vector256.IsHardwareAccelerated,
                ServerGc = GCSettings.IsServerGC, LatencyMode = GCSettings.LatencyMode.ToString(), Samples = 4,
                MaximumBgraSamples = gpu.Context.GetMaxSurfaceSampleCount(SkiaSharp.SKColorType.Bgra8888),
                SkiaCacheLimitBytes = gpu.Context.GetResourceCacheLimit(), Width = 800, Height = 600, PlotScale = 1,
                Workload = "Full Figure: 32x20000 SignalXY, Bars/Text/axes/title/grid/legend; Line AA=true, width2. All sampling and paths rebuilt every frame.",
                GpuToCpuReadbacksInTimedFrames = 0, DiagnosticReadbacks = Variants.Length,
                Measurement = "Wall time from full Plot.Render through Flush/Submit and explicit D3D12 fence completion; no GPU timestamps; no Present/compositor. Data mutation time is measured separately.",
                NoNativeSoftwareFallbackTrace = "Not captured. Hardware GPU surface is checked, but Skia internal path selection is not yet proven for all styles.",
                StageCounterOverhead = "One timer per source and bulk-path call; original variant has no such counters.",
                Cases = summary });
            Console.WriteLine(JsonSerializer.Serialize(summary, Json));
            return 0;
        }
        catch (Exception ex) { Save(evidence, "failure.json", new { Exception = ex.ToString() }); Console.Error.WriteLine(ex); return 1; }
    }

    private static void Stages(GpuHarness gpu, GpuTarget target, string evidence)
    {
        List<object> results = [];
        foreach (Variant variant in new[] { Variants[0], Variants[^1] })
        {
            using FigureFixture figure = new(variant);
            foreach (var path in figure.Paths) path.MeasureDrawCalls = true;
            List<object> frames = [];
            for (int i = -30; i < 120; i++)
            {
                figure.ClearCounters();
                foreach (var path in figure.Paths) path.DrawTicks = 0;
                long start = Stopwatch.GetTimestamp();
                target.Render(figure.Plot);
                long rendered = Stopwatch.GetTimestamp();
                gpu.Context.Flush(true, false); gpu.Wait();
                long completed = Stopwatch.GetTimestamp();
                if (i >= 0) frames.Add(new { TotalMs = Ms(completed - start), PlotMs = Ms(rendered - start),
                    DataMs = Ms(figure.Sources.Sum(s => s.DataTicks)), BulkPathMs = Ms(figure.Paths.Sum(s => s.PathTicks)),
                    NativeDrawPathWallMs = Ms(figure.Paths.Sum(s => s.DrawTicks)),
                    Actions = figure.Plot.RenderManager.LastRender.TimedActions.ToDictionary(a => a.Item1, a => a.Item2.TotalMilliseconds) });
            }
            results.Add(new { variant.Name, Frames = frames });
        }
        Save(evidence, "stages.json", new { Note = "Additional diagnostic run, with extra timers; native DrawPath wall time is not GPU timestamp time.", Cases = results });
        Console.WriteLine("Stage diagnosis complete.");
    }
    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] sorted = values.Order().ToArray();
        double index = (sorted.Length - 1) * percentile;
        int lo = (int)index, hi = (int)Math.Ceiling(index);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (index - lo);
    }
    private static void Save(string directory, string name, object value) => File.WriteAllText(Path.Combine(directory, name), JsonSerializer.Serialize(value, Json));
    /// <summary>Stores one synchronized full-Figure measurement.</summary>
    private sealed record Frame(int Index, double TotalMs, double PlotMs, double FlushMs, double FenceMs, long AllocatedBytes,
        double UpdateOutsideRenderMs, double DataMs, double BulkPathMs, int EffectiveLinePoints, long VectorRanges, long SpecialScalarRanges);
    /// <summary>Stores a warmup-separated benchmark round.</summary>
    private sealed record Case(int Round, string Scenario, string Variant, int Warmup, List<Frame> Frames);
}



