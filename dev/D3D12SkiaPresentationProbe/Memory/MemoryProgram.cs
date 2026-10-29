using System.Text.Json;
using SkiaSharp;

/// <summary>Separately measures exact-size and growable GPU target lifetimes, then checks viewport pixels.</summary>
internal static class MemoryProgram
{
    private static int Main(string[] args)
    {
        string evidence = Path.GetFullPath(args[0]), mode = args.Length > 1 ? args[1] : "capacity";
        Directory.CreateDirectory(evidence);
        try
        {
            using GpuHarness gpu = new();
            using FigureFixture figure = new(new("presentation", true, true, true, true, true));
            using GpuFigureSurface capacity = new(gpu.Device, gpu.Context, gpu.Queue);
            using ProbeMemory memory = new(gpu.Adapter, gpu.Context);
            if (mode == "wrapped-texture-handoff")
            {
                List<object> cases = [];
                foreach (string flush in new[] { "context", "surface", "snapshot" })
                {
                    using GpuTarget wrapped = new(gpu);
                    wrapped.Render(figure.Plot);
                    using SKImage? image = flush == "snapshot" ? wrapped.Surface.Snapshot() : null;
                    if (flush == "surface") wrapped.Surface.Flush(true, false); else gpu.Context.Flush(true, false);
                    gpu.Wait();
                    cases.Add(new { Mode = flush, Texture = ExternalTextureDiagnostic.Save(gpu.Device, gpu.Queue, gpu.Wait, wrapped.Resource, 800, 600, Path.Combine(evidence, "wrapped-handoff-" + flush + ".png")) });
                }
                Save(evidence, "wrapped-handoff.json", cases); return 0;
            }
            if (mode == "resolved-performance")
            {
                List<object> cases = [];
                capacity.Ensure(800, 600);
                foreach (string scenario in new[] { "full-redraw", "all-y-updated-in-place" })
                {
                    List<double> totals = [], updates = [];
                    List<object> measuredFrames = [];
                    List<long> allocated = [];
                    for (int round = 0; round < 3; round++)
                    for (int i = -30; i < 120; i++)
                    {
                        long beginUpdate = System.Diagnostics.Stopwatch.GetTimestamp();
                        if (scenario != "full-redraw") figure.UpdateData(i + round * 120 + 30);
                        double updateMs = System.Diagnostics.Stopwatch.GetElapsedTime(beginUpdate).TotalMilliseconds;
                        long bytes = GC.GetAllocatedBytesForCurrentThread();
                        long start = System.Diagnostics.Stopwatch.GetTimestamp();
                        capacity.Render(figure.Plot, 800, 600);
                        long rendered = System.Diagnostics.Stopwatch.GetTimestamp();
                        capacity.SubmitForExternalUse();
                        long submitted = System.Diagnostics.Stopwatch.GetTimestamp();
                        gpu.Wait();
                        long done = System.Diagnostics.Stopwatch.GetTimestamp();
                        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
                        if (i >= 0)
                        {
                            double total = System.Diagnostics.Stopwatch.GetElapsedTime(start, done).TotalMilliseconds;
                            totals.Add(total); updates.Add(total + updateMs); allocated.Add(bytes);
                            measuredFrames.Add(new { Round = round + 1, Frame = i, TotalMs = total, UpdateOutsideRenderMs = updateMs, AllocatedBytes = bytes,
                                PlotMs = System.Diagnostics.Stopwatch.GetElapsedTime(start, rendered).TotalMilliseconds,
                                FlushAndResolveSubmitMs = System.Diagnostics.Stopwatch.GetElapsedTime(rendered, submitted).TotalMilliseconds,
                                ActualFenceWaitMs = System.Diagnostics.Stopwatch.GetElapsedTime(submitted, done).TotalMilliseconds });
                        }
                    }
                    double Q(IEnumerable<double> values, double p) { double[] sorted = values.Order().ToArray(); double k = (sorted.Length - 1) * p; int lo = (int)k, hi = (int)Math.Ceiling(k); return sorted[lo] + (sorted[hi] - sorted[lo]) * (k - lo); }
                    cases.Add(new { Scenario = scenario, Frames = measuredFrames, MedianMs = Q(totals, .5), P95Ms = Q(totals, .95), P99Ms = Q(totals, .99),
                        MaxMs = totals.Max(), MedianAllocatedBytes = Q(allocated.Select(x => (double)x), .5),
                        UpdateIncludedMedianMs = Q(updates, .5), UpdateIncludedP95Ms = Q(updates, .95), Over5ms = totals.Count(x => x > 5) });
                }
                object texture = ExternalTextureDiagnostic.Save(gpu.Device, gpu.Queue, gpu.Wait, capacity.Resource!, 800, 600, Path.Combine(evidence, "resolved-external.png"));
                Save(evidence, "resolved-performance.json", new { Backend = gpu.Context.Backend.ToString(), NativeMsaaSamples = 4,
                    ExplicitGpuResolve = true, FenceEvent = "AutoResetEvent", TimedPixelReadbacks = 0, DiagnosticReadbacksAfterTiming = 1,
                    Measurement = "Full Figure through explicit native MSAA resolve and verified fence completion; excludes Present/compositor.",
                    capacity.ResolveSubmissions, ExternalTextureProof = texture, Cases = cases }); return 0;
            }
            if (mode == "handoff")
            {
                List<object> cases = [];
                foreach (string flush in new[] { "context", "surface", "snapshot", "explicit-native-resolve" })
                {
                    using GpuFigureSurface handoff = new(gpu.Device, gpu.Context, gpu.Queue);
                    handoff.Ensure(800, 600);
                    handoff.Render(figure.Plot, 800, 600);
                    using SKImage? snapshot = flush == "snapshot" ? handoff.Surface!.Snapshot() : null;
                    if (flush == "explicit-native-resolve") handoff.SubmitForExternalUse(); else if (flush == "surface") handoff.Surface!.Flush(true, false); else gpu.Context.Flush(true, false);
                    gpu.Wait();
                    cases.Add(new { Mode = flush, Texture = ExternalTextureDiagnostic.Save(gpu.Device, gpu.Queue, gpu.Wait, handoff.Resource!, 800, 600, Path.Combine(evidence, "handoff-" + flush + ".png")) });
                }
                Save(evidence, "handoff.json", cases);
                return 0;
            }
            if (mode == "visual")
            {
                List<object> images = [];
                foreach (var size in new[] { (W: 800, H: 600, Dpi: 1.0), (W: 1234, H: 789, Dpi: 1.5), (W: 640, H: 480, Dpi: 2.0), (W: 800, H: 600, Dpi: 1.0) })
                {
                    figure.Plot.ScaleFactor = size.Dpi;
                    gpu.Wait(); capacity.Ensure(size.W, size.H);
                    using GpuTarget exact = new(gpu, size.W, size.H);
                    for (int warm = 0; warm < 4; warm++) { exact.Render(figure.Plot); gpu.Context.Flush(true, false); gpu.Wait(); }
                    byte[] reference = exact.DiagnosticPixels(Path.Combine(evidence, $"exact-{images.Count}.png"));
                    for (int warm = 0; warm < 4; warm++) { capacity.Render(figure.Plot, size.W, size.H); gpu.Context.Flush(true, false); gpu.Wait(); }
                    using SKImage snapshot = capacity.Surface!.Snapshot();
                    using SKBitmap bitmap = new(new SKImageInfo(size.W, size.H, SKColorType.Bgra8888, SKAlphaType.Premul));
                    if (!snapshot.ReadPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, 0, 0)) throw new InvalidOperationException("Diagnostic read failed.");
                    byte[] pixels = bitmap.Bytes;
                    int differentBytes = 0, maxDelta = 0; long absoluteError = 0;
                    for (int i = 0; i < pixels.Length; i++) if (pixels[i] != reference[i]) { int delta = Math.Abs(pixels[i] - reference[i]); differentBytes++; maxDelta = Math.Max(maxDelta, delta); absoluteError += delta; }
                    using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    using Stream stream = File.Create(Path.Combine(evidence, $"capacity-{images.Count}.png")); png.SaveTo(stream);
                    images.Add(new { size.W, size.H, size.Dpi, CapacityW = capacity.Width, CapacityH = capacity.Height, DifferentBytes = differentBytes, MaxChannelDelta = maxDelta, MeanAbsoluteChannelError = (double)absoluteError / pixels.Length, ReferenceClip = exact.Surface.Canvas.LocalClipBounds.ToString(), CapacityClip = capacity.Surface.Canvas.LocalClipBounds.ToString() });
                }
                Save(evidence, "viewport-visual.json", new { Result = "DIAGNOSTIC_REVIEW_REQUIRED", DiagnosticReadbacksOutsideTiming = 8, Cases = images });
                return 0;
            }
            List<object> samples = [];
            GpuTarget? exactTarget = null;
            int currentWidth = 0, currentHeight = 0, exactAllocations = 0, frames = 0;
            void Frame(int width, int height)
            {
                if (mode == "capacity") { gpu.Wait(); capacity.Ensure(width, height); capacity.Render(figure.Plot, width, height); }
                else
                {
                    if (exactTarget is null || currentWidth != width || currentHeight != height)
                    {
                        gpu.Wait(); exactTarget?.Dispose(); exactTarget = new(gpu, width, height); exactAllocations++;
                        currentWidth = width; currentHeight = height;
                    }
                    exactTarget.Render(figure.Plot);
                }
                gpu.Context.Flush(true, false); gpu.Wait(); frames++;
            }
            void Sample(string stage) => samples.Add(new { Stage = stage, Frames = frames,
                ExactTargetAllocations = exactAllocations, Data = memory.Sample(stage, capacity, currentWidth, currentHeight) });
            try
            {
                for (int i = 0; i < 30; i++) Frame(800, 600);
                Sample("warmup");
                for (int cycle = 1; cycle <= 12; cycle++)
                {
                    for (int i = 0; i < 24; i++) Frame(640 + i * 27, 480 + i * 17);
                    Sample("resize-" + cycle);
                }
                for (int i = 0; i < 200; i++) Frame(800, 600);
                Sample("returned-to-800x600");
                exactTarget?.Dispose(); exactTarget = null; capacity.Release(); gpu.Context.PurgeResources(); gpu.Wait();
                Sample("explicit-release-and-purge");
                Save(evidence, "memory-" + mode + ".json", new { Result = "PASS_RUN_ONLY", Mode = mode, Backend = gpu.Context.Backend.ToString(),
                    CacheLimit = gpu.Context.GetResourceCacheLimit(), PixelReadbacks = 0, ForcedGc = false, Samples = samples });
            }
            finally { exactTarget?.Dispose(); }
            return 0;
        }
        catch (Exception ex) { Save(evidence, "failure-" + mode + ".json", new { Exception = ex.ToString() }); Console.Error.WriteLine(ex); return 1; }
    }
    private static void Save(string dir, string name, object value) => File.WriteAllText(Path.Combine(dir, name), JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}





