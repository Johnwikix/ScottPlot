using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using ScottPlot;
using ScottPlot.Interactivity.UserActions;

namespace AvaloniaD3D12Probe;

/// <summary>Runs complete-frame measurements and records real-window validation evidence.</summary>
internal sealed class ProbeRunner(ProbeWindow window)
{
    private readonly List<object> Checks = [];
    private readonly List<FrameSample> Samples = [];
    private int DiagnosticSnapshots;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static double Elapsed(long start) => Stopwatch.GetElapsedTime(start).TotalMilliseconds;

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmFlush();

    internal void SaveEnvironment() => Save("environment.json", new
    {
        Runtime = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
        Avalonia = typeof(Application).Assembly.GetName().Version?.ToString(),
        SkiaSharp = typeof(SkiaSharp.SKSurface).Assembly.GetName().Version?.ToString(),
        window.Gpu.AdapterName, window.Witness.Backend, window.RenderScaling,
        Screens = window.Screens.All.Select(s => new { s.Bounds, s.WorkingArea, s.Scaling, s.IsPrimary }),
        Scene = "32 x 20,000 SignalXY, in-place Y updates, width 2, AA, bars/text/axes/title/grid/legend",
        RenderingMode = "AngleEgl only; software fallback disabled",
        Boundary = "Invalidate/render request through CompositionBatch.Rendered, then DwmFlush on a worker. Not photon latency or GPU timestamp timing.",
    });

    internal void SaveInteraction() => Save("interaction.json", new
    {
        window.Gpu.PointerWheelEvents, window.Gpu.PointerMoveEvents, window.Gpu.Frames,
        window.Gpu.Readbacks, window.Gpu.Copies, window.Gpu.CompletedUpdates,
        window.RenderScaling, window.Gpu.RenderScale, window.Gpu.LastSize,
        Limits = Limits(window.Gpu.Plot.Axes.GetLimits()), Failure = window.Gpu.Failure?.ToString(),
    });

    internal async Task SnapshotAsync(string name)
    {
        await window.Compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(TimeSpan.FromSeconds(20));
        // Explicit one-shot composition snapshot, outside every measured render/present loop.
        using var bitmap = await window.Compositor.CreateCompositionVisualSnapshot(
            ElementComposition.GetElementVisual(window)!, window.RenderScaling);
        bitmap.Save(Path.Combine(Program.Output, name + ".png"));
        DiagnosticSnapshots++;
        SaveInteraction();
    }

    internal async Task ValidateAsync()
    {
        window.SetStatus("Validation: real GPU composition, render scales, anchored zoom and resize");
        Checks.Clear();
        window.SizeToContent = Avalonia.Controls.SizeToContent.WidthAndHeight;
        window.Viewport.Width = 800; window.Viewport.Height = 600;
        await window.SelectAsync(true, false);
        window.Gpu.AutomaticRefresh = false;
        double originalWidth = window.Viewport.Width, originalHeight = window.Viewport.Height;
        PixelPoint originalPosition = window.Position;
        try
        {
            await SnapshotAsync("initial-d3d12");
            foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
            {
                window.Gpu.TestRenderScale = scale;
                window.GpuFigure.ResetLimits();
                await FrameAsync("validation", false, 0, 0);
                Pixel pixel = window.Gpu.PhysicalPixel(new Point(window.Gpu.Bounds.Width * .55, window.Gpu.Bounds.Height * .55));
                Coordinates before = window.Gpu.Plot.GetCoordinates(pixel);
                double widthBefore = window.Gpu.Plot.Axes.GetLimits().HorizontalSpan;
                window.Gpu.UserInputProcessor.Process(new MouseWheelUp(pixel));
                await FrameAsync("validation", false, 0, 0);
                Coordinates after = window.Gpu.Plot.GetCoordinates(pixel);
                double widthAfter = window.Gpu.Plot.Axes.GetLimits().HorizontalSpan;
                bool anchor = Math.Abs(before.X - after.X) < 1e-7 && Math.Abs(before.Y - after.Y) < 1e-7;
                bool dimensions = window.Gpu.LastSize.Width == (int)Math.Ceiling(window.Gpu.Bounds.Width * scale) &&
                    window.Gpu.LastSize.Height == (int)Math.Ceiling(window.Gpu.Bounds.Height * scale);
                Require(anchor && dimensions && widthAfter < widthBefore,
                    $"Render scale {scale}: size or anchored zoom failed");
                Checks.Add(new { Case = "controlled-render-scale-and-zoom", Scale = scale,
                    Note = "Exercises DPI sizing/input math; does not change OS or monitor DPI",
                    window.Gpu.LastSize, Before = new { before.X, before.Y }, After = new { after.X, after.Y }, WidthBefore = widthBefore, WidthAfter = widthAfter, Pass = true });
                await SnapshotAsync("scale-" + scale.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            }
            window.Gpu.TestRenderScale = null;
            window.GpuFigure.ResetLimits();
            for (int cycle = 0; cycle < 6; cycle++)
            {
                foreach ((int width, int height) in new[] { (640, 420), (960, 540), (321, 257), (800, 600) })
                {
                    window.Viewport.Width = width; window.Viewport.Height = height;
                    window.UpdateLayout();
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    await FrameAsync("validation", false, 0, cycle);
                    Require(window.Gpu.LastSize.Width == (int)Math.Ceiling(window.Gpu.Bounds.Width * window.RenderScaling) &&
                        window.Gpu.LastSize.Height == (int)Math.Ceiling(window.Gpu.Bounds.Height * window.RenderScaling), "Resize viewport mismatch");
                    Checks.Add(new { Case = "resize", Cycle = cycle, Width = width, Height = height,
                        window.Gpu.LastSize, window.Gpu.TextureAllocations, Pass = true });
                }
            }
            await SnapshotAsync("after-resize-d3d12");
            foreach (var screen in window.Screens.All)
            {
                window.Position = new(screen.WorkingArea.X + 30, screen.WorkingArea.Y + 30);
                await Task.Delay(350);
                await FrameAsync("validation", false, 0, 0);
                Checks.Add(new { Case = "actual-monitor-dpi", Expected = screen.Scaling, Actual = window.RenderScaling,
                    window.Gpu.LastSize, Pass = Math.Abs(screen.Scaling - window.RenderScaling) < .001 });
                Require(Math.Abs(screen.Scaling - window.RenderScaling) < .001, "Monitor DPI did not match the window");
                await SnapshotAsync("monitor-" + screen.Scaling.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            }
            await window.SelectAsync(false, false);
            await SnapshotAsync("existing-avaplot");
            await window.SelectAsync(true, false);
            await FrameAsync("validation", false, 0, 0);
            Require(window.Gpu.Readbacks == 0, "A presentation frame read pixels back");
            Require(window.Gpu.Copies == window.Gpu.CompletedUpdates, "GPU copies/import completions do not match");
            Checks.Add(new { Case = "no-per-frame-readback", window.Gpu.Readbacks, window.Gpu.Copies,
                window.Gpu.CompletedUpdates, window.Gpu.Frames, DiagnosticSnapshots, Pass = true });
            Save("validation.json", new { Pass = true, Checks, DiagnosticSnapshots,
                ActualDpiTransitionTested = window.Screens.All.Select(s => s.Scaling).Distinct().Count() > 1 });
            window.SetStatus("Validation PASS: GPU interop, anchored zoom, render scales and 24 resize transitions; presentation readbacks = 0");
        }
        catch (Exception error)
        {
            Save("validation.json", new { Pass = false, Checks, Error = error.ToString() });
            throw;
        }
        finally
        {
            window.Gpu.TestRenderScale = null;
            window.Position = originalPosition;
            window.Viewport.Width = originalWidth; window.Viewport.Height = originalHeight;
            window.UpdateLayout();
            window.Gpu.AutomaticRefresh = true;
        }
    }

    internal async Task BenchmarkAsync()
    {
        int frames = int.Parse(Program.Option("--frames") ?? "60");
        int warmup = int.Parse(Program.Option("--warmup") ?? "10");
        int rounds = int.Parse(Program.Option("--rounds") ?? "3");
        Require(frames > 0 && warmup >= 0 && rounds > 0, "Invalid benchmark sample counts");
        Samples.Clear();
        window.SizeToContent = Avalonia.Controls.SizeToContent.WidthAndHeight;
        window.Gpu.TestRenderScale = null;
        window.Viewport.Width = 800; window.Viewport.Height = 600; window.UpdateLayout();
        string[] backends = ["avaplot", "d3d12", "d3d12-optimized"];
        for (int round = 0; round < rounds; round++)
        {
            foreach (string backend in round % 2 == 0 ? backends : backends.Reverse())
            {
                await window.SelectAsync(backend != "avaplot", backend == "d3d12-optimized");
                window.Gpu.AutomaticRefresh = false;
                window.BaselineFigure.ResetLimits(); window.GpuFigure.ResetLimits();
                foreach (bool update in new[] { false, true })
                {
                    window.SetStatus($"Measuring {backend}, {(update ? "updated" : "static")}, round {round + 1}/{rounds}, {frames} frames");
                    for (int frame = -warmup; frame < frames; frame++)
                    {
                        FrameSample sample = await FrameAsync(backend, update, round, frame + warmup);
                        if (frame >= 0) Samples.Add(sample);
                        if (window.Gpu.Failure is { } failure) throw new InvalidOperationException("An automatic frame failed", failure);
                    }
                    SaveBenchmark(frames, warmup, rounds);
                }
            }
        }
        await window.SelectAsync(true, true);
        window.SetStatus("Benchmark complete. See benchmark.json and frames.csv; all measured frames performed a full Figure redraw.");
    }

    private async Task<FrameSample> FrameAsync(string backend, bool update, int round, int frame)
    {
        FigureFixture fixture = window.IsGpu ? window.GpuFigure : window.BaselineFigure;
        int rendered = fixture.Plot.RenderManager.RenderCount;
        long drawTicks = Interlocked.Read(ref fixture.DrawTicks);
        long readbacks = window.Gpu.Readbacks;
        long start = Stopwatch.GetTimestamp();
        if (update) lock (fixture.Plot.Sync) fixture.Update(frame);
        if (window.IsGpu) await window.Gpu.RenderFrameAsync();
        else
        {
            window.Baseline.InvalidateVisual();
            // Capture this request's batch immediately, rather than timing a subsequent empty batch.
            await window.Compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(TimeSpan.FromSeconds(20));
        }
        double composition = Elapsed(start);
        int dwmResult = await Task.Run(DwmFlush);
        Marshal.ThrowExceptionForHR(dwmResult);
        double complete = Elapsed(start);
        int redraws = fixture.Plot.RenderManager.RenderCount - rendered;
        Require(redraws > 0, "Composition batch completed without a full Figure redraw");
        Require(window.Gpu.Readbacks == readbacks, "Readback count changed inside a measured frame");
        return new(backend, update ? "updated" : "static", round, frame, composition, complete,
            (Interlocked.Read(ref fixture.DrawTicks) - drawTicks) * 1000.0 / Stopwatch.Frequency,
            window.IsGpu ? window.Gpu.RenderMilliseconds : null, redraws,
            window.Gpu.Readbacks - readbacks, window.RenderScaling,
            (int)Math.Ceiling(window.Viewport.Bounds.Width * window.RenderScaling),
            (int)Math.Ceiling(window.Viewport.Bounds.Height * window.RenderScaling));
    }

    private void SaveBenchmark(int frames, int warmup, int rounds)
    {
        Save("benchmark.json", new
        {
            Scene = "32 x 20,000 / complete Figure / width 2 / AA",
            frames, warmup, rounds,
            D3D12MsaaSamples = 4,
            AvaloniaSampleCount = "Owned by Avalonia/ANGLE; not forced to match D3D12 MSAA4",
            Timing = "Sequential request-to-CompositionBatch.Rendered, followed by DwmFlush; includes scheduling, full redraw and presentation submission. DWM/vsync can dominate. No hardware scanout timestamps.",
            UpdateTiming = "Updated samples include in-place array mutation. Static samples still redraw the complete Figure.",
            DiagnosticSnapshotsExcluded = true,
            PresentationReadbacks = Samples.Sum(s => s.Readbacks),
            Summary = Samples.GroupBy(s => new { s.Backend, s.Scenario }).Select(g => new
            {
                g.Key.Backend, g.Key.Scenario, Count = g.Count(),
                CompositionMedianMs = Percentile(g.Select(s => s.CompositionMs), .5),
                CompositionP95Ms = Percentile(g.Select(s => s.CompositionMs), .95),
                CompleteMedianMs = Percentile(g.Select(s => s.CompleteMs), .5),
                CompleteP95Ms = Percentile(g.Select(s => s.CompleteMs), .95),
                DrawMedianMs = Percentile(g.Select(s => s.DrawMs), .5),
                RendererMedianMs = g.First().RendererMs.HasValue ? Percentile(g.Select(s => s.RendererMs!.Value), .5) : (double?)null,
                MinRedraws = g.Min(s => s.Redraws), MaxRedraws = g.Max(s => s.Redraws),
                g.First().Scale, g.First().Width, g.First().Height,
            }),
            Samples,
        });
        string header = "backend,scenario,round,frame,composition_ms,complete_ms,draw_ms,renderer_ms,redraws,readbacks,scale,width,height";
        File.WriteAllLines(Path.Combine(Program.Output, "frames.csv"), new[] { header }.Concat(Samples.Select(s =>
            FormattableString.Invariant($"{s.Backend},{s.Scenario},{s.Round},{s.Frame},{s.CompositionMs:F6},{s.CompleteMs:F6},{s.DrawMs:F6},{s.RendererMs:F6},{s.Redraws},{s.Readbacks},{s.Scale},{s.Width},{s.Height}"))));
    }

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        double[] ordered = values.Order().ToArray();
        double index = (ordered.Length - 1) * percentile;
        int low = (int)index, high = (int)Math.Ceiling(index);
        return ordered[low] + (ordered[high] - ordered[low]) * (index - low);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static object Limits(AxisLimits limits) => new { limits.Left, limits.Right, limits.Bottom, limits.Top };

    private static void Save(string name, object value) => File.WriteAllText(Path.Combine(Program.Output, name), JsonSerializer.Serialize(value, JsonOptions));

    /// <summary>Records a full Figure redraw and its common composition/presentation barriers.</summary>
    private sealed record FrameSample(string Backend, string Scenario, int Round, int Frame,
        double CompositionMs, double CompleteMs, double DrawMs, double? RendererMs,
        int Redraws, long Readbacks, double Scale, int Width, int Height);
}
