using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ScottPlot;
using ScottPlot.Plottables;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

/// <summary>Attributes the full-Figure regression using isolated diagnostic variants without changing ScottPlot core.</summary>
internal static class ProfileProgram
{
    private static int Main(string[] args)
    {
        string evidence = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(evidence);
        using IDXGIFactory4 factory = CreateDXGIFactory2<IDXGIFactory4>(false);
        factory.EnumAdapters1(0, out IDXGIAdapter1 adapter).CheckError();
        using (adapter)
        using (ID3D12Device device = D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0))
        using (ID3D12CommandQueue queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct)))
        using (ID3D12Fence fence = device.CreateFence(0))
        using (ManualResetEvent wait = new(false))
        using (GRD3DBackendContext backend = new() { Adapter = adapter.NativePointer, Device = device.NativePointer, Queue = queue.NativePointer })
        using (GRContext context = GRContext.CreateDirect3D(backend) ?? throw new InvalidOperationException("No D3D12 context."))
        {
            if (context.Backend != GRBackend.Direct3D || (adapter.Description1.Flags & AdapterFlags.Software) != 0)
                throw new InvalidOperationException("Hardware D3D12 required.");
            List<object> cases = new();
            ulong sequence = 0;
            var variants = new[]
            {
                new Variant("original-aa-single", true, 0, false),
                new Variant("original-noaa-single", false, 0, false),
                new Variant("original-aa-msaa4", true, 4, false),
                new Variant("staged-aa-single", true, 0, true),
                new Variant("staged-noaa-single", false, 0, true),
                new Variant("staged-aa-msaa4", true, 4, true),
            };
            foreach (Variant variant in variants)
            {
                Console.WriteLine(variant.Name);
                using Plot plot = (Plot)typeof(Program).GetMethod("CreateDensePlot", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
                StageTimes stages = new();
                for (int i = 0; i < plot.PlottableList.Count; i++)
                {
                    if (plot.PlottableList[i] is SignalXY signal)
                    {
                        signal.LineStyle.AntiAlias = variant.Aa;
                        if (variant.Staged) plot.PlottableList[i] = new MeasuredSignal(signal, stages);
                    }
                }
                using ID3D12Resource target = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
                    ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, 800, 600, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget), ResourceStates.RenderTarget);
                using GRD3DTextureResourceInfo info = new() { Resource = target.NativePointer, ResourceState = (uint)ResourceStates.RenderTarget,
                    Format = (uint)Format.B8G8R8A8_UNorm, SampleCount = 1, LevelCount = 1 };
                using GRBackendTexture texture = new(800, 600, info);
                using SKSurface surface = SKSurface.Create(context, texture, GRSurfaceOrigin.TopLeft, variant.Samples, SKColorType.Bgra8888)
                    ?? throw new InvalidOperationException($"Cannot create surface for {variant.Name}; no fallback.");
                if (surface.Context?.Backend != GRBackend.Direct3D || surface.PeekPixels() is not null)
                    throw new InvalidOperationException("GPU surface required.");
                List<object> frames = new();
                for (int i = -6; i < 12; i++)
                {
                    stages.Clear();
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    long start = Stopwatch.GetTimestamp();
                    int save = surface.Canvas.Save();
                    try { plot.Render(surface); }
                    finally { surface.Canvas.RestoreToCount(save); }
                    long rendered = Stopwatch.GetTimestamp();
                    context.Flush(true, false);
                    long submitted = Stopwatch.GetTimestamp();
                    queue.Signal(fence, ++sequence).CheckError();
                    if (fence.CompletedValue < sequence)
                    {
                        fence.SetEventOnCompletion(sequence, wait.SafeWaitHandle.DangerousGetHandle()).CheckError();
                        if (!wait.WaitOne(TimeSpan.FromSeconds(20))) throw new TimeoutException("Fence timeout.");
                    }
                    long complete = Stopwatch.GetTimestamp();
                    long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
                    device.DeviceRemovedReason.CheckError();
                    if (context.IsAbandoned) throw new InvalidOperationException("GPU context abandoned.");
                    if (i >= 0)
                    {
                        frames.Add(new
                        {
                            Frame = i, PlotRenderMs = Ms(rendered - start), FlushSubmitMs = Ms(submitted - rendered),
                            ExplicitFenceMs = Ms(complete - submitted), TotalMs = Ms(complete - start), AllocatedBytes = allocated,
                            DataPixelsMs = Ms(stages.Data), PathConstructionMs = Ms(stages.Path),
                            NativeDrawPathMs = Ms(stages.Draw), PaintSetupMs = Ms(stages.Paint), MarkersMs = Ms(stages.Markers),
                            EffectiveLinePoints = stages.Points, PlottableRenderCalls = stages.Calls,
                            Actions = plot.RenderManager.LastRender.TimedActions.ToDictionary(x => x.Item1, x => x.Item2.TotalMilliseconds),
                        });
                    }
                }
                // Explicit diagnostic PNG after timing, never a presentation or per-frame readback.
                using (SKImage image = surface.Snapshot())
                using (SKData png = image.Encode())
                using (Stream output = File.Create(Path.Combine(evidence, variant.Name + ".png"))) png.SaveTo(output);
                cases.Add(new { variant.Name, LineAntiAlias = variant.Aa, RequestedSurfaceSamples = variant.Samples,
                    InstrumentedStraightSignalBody = variant.Staged, Warmup = 6, Measured = 12, Frames = frames });
            }
            File.WriteAllText(Path.Combine(evidence, "profile.json"), JsonSerializer.Serialize(new
            {
                Backend = context.Backend.ToString(), Adapter = adapter.Description1.Description,
                MaximumBgraSampleCount = context.GetMaxSurfaceSampleCount(SKColorType.Bgra8888),
                Workload = "Same original CreateDensePlot fixture: full Figure, 32x20000 SignalXY, bars, text, axes, title, grid, legend; 800x600.",
                MeasurementNote = "Wall times; native DrawPath and Flush may contain internal Skia/driver waits. No GPU timestamp attribution.",
                InstrumentationNote = "Staged variants reproduce only the fixed straight SignalXY/Drawing.DrawLines body with stage counters; original variants use unmodified SignalXY.Render.",
                PixelReadbacksDuringTimedFrames = 0, DiagnosticPngAfterEachCase = true, Cases = cases,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        return 0;
    }

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>Describes one full-Figure diagnostic variant.</summary>
    private sealed record Variant(string Name, bool Aa, int Samples, bool Staged);

    /// <summary>Accumulates stage times across all 32 ordinary straight lines in one Figure.</summary>
    private sealed class StageTimes
    {
        internal long Data, Path, Draw, Paint, Markers, Points, Calls;
        internal void Clear() { Data = Path = Draw = Paint = Markers = Points = Calls = 0; }
    }

    /// <summary>Times equivalent operations from SignalXY.Render and Drawing.DrawLines for the fixed straight-line fixture.</summary>
    private sealed class MeasuredSignal(SignalXY source, StageTimes times) : IPlottable
    {
        private readonly ScottPlot.PathStrategies.Straight Strategy = new();
        /// <inheritdoc />
        public bool IsVisible { get => source.IsVisible; set => source.IsVisible = value; }
        /// <inheritdoc />
        public IAxes Axes { get => source.Axes; set => source.Axes = value; }
        /// <inheritdoc />
        public IEnumerable<LegendItem> LegendItems => source.LegendItems;
        /// <inheritdoc />
        public AxisLimits GetAxisLimits() => source.GetAxisLimits();
        /// <inheritdoc />
        public void Render(RenderPack rp)
        {
            if (!source.IsVisible || source.Data.Count == 0) return;
            if (source.ConnectStyle != ConnectStyle.Straight) throw new InvalidOperationException("Diagnostic only supports straight fixture.");
            times.Calls++;
            long start = Stopwatch.GetTimestamp();
            IReadOnlyList<Pixel> pixels = source.Data.GetPixelsToDraw(rp, source.Axes, source.ConnectStyle);
            times.Data += Stopwatch.GetTimestamp() - start;
            times.Points += pixels.Count;
            if (source.LineStyle.CanBeRendered && pixels.Take(2).Count() >= 2)
            {
                start = Stopwatch.GetTimestamp();
                source.LineStyle.ApplyToPaint(rp.Paint);
                if (source.LineStyle.Hairline) rp.Paint.StrokeWidth = 1f / rp.Canvas.TotalMatrix.ScaleX;
                times.Paint += Stopwatch.GetTimestamp() - start;
                start = Stopwatch.GetTimestamp();
                using SKPath path = Strategy.GetPath(pixels);
                times.Path += Stopwatch.GetTimestamp() - start;
                start = Stopwatch.GetTimestamp();
                rp.Canvas.DrawPath(path, rp.Paint.SKPaint);
                times.Draw += Stopwatch.GetTimestamp() - start;
            }
            start = Stopwatch.GetTimestamp();
            Drawing.DrawMarkers(rp.Canvas, rp.Paint, pixels, source.MarkerStyle);
            times.Markers += Stopwatch.GetTimestamp() - start;
        }
    }
}

