using System.IO;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.DXGI;
using static Vortice.Direct3D12.D3D12;
using static Vortice.DXGI.DXGI;

/// <summary>Checks the shipped SkiaSharp D3D12 path without changing ScottPlot or loading the curve extension.</summary>
internal static class Program
{
    private static readonly Dictionary<string, object?> Evidence = new();
    private static string EvidenceDirectory = "";
    private static string EvidenceName = "offscreen";
    private static string CurrentStage = "start";
    private static string Stage { get => CurrentStage; set { CurrentStage = value; Evidence["stage"] = value; Console.WriteLine(value); Save(); } }

    /// <summary>Runs the isolated probe and returns failure if any required GPU property is absent.</summary>
    private static int Main(string[] args)
    {
        EvidenceDirectory = Path.GetFullPath(args.Length > 0 ? args[0] : "../../artifacts/d3d12-skia-probe/2026-10-28");
        Directory.CreateDirectory(EvidenceDirectory);
        bool dense = args.Skip(1).Contains("dense");
        EvidenceName = dense ? "offscreen-dense" : "offscreen";
        try
        {
            Stage = "api";
            CheckApi();
            Stage = "hardware-device";
            using IDXGIFactory4 factory = CreateDXGIFactory2<IDXGIFactory4>(false);
            using IDXGIAdapter1 adapter = SelectHardwareAdapter(factory);
            AdapterDescription1 description = adapter.Description1;
            Evidence["adapter"] = new { description.Description, description.VendorId, description.DeviceId, Flags = description.Flags.ToString() };
            using ID3D12Device device = D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_11_0);
            using ID3D12CommandQueue queue = device.CreateCommandQueue(new CommandQueueDescription(CommandListType.Direct));
            using ID3D12Fence fence = device.CreateFence(0);
            using ManualResetEvent fenceEvent = new(false);
            Stage = "skia-context";
            using GRD3DBackendContext backend = new() { Adapter = adapter.NativePointer, Device = device.NativePointer, Queue = queue.NativePointer };
            using GRContext context = GRContext.CreateDirect3D(backend)
                ?? throw new InvalidOperationException("CreateDirect3D returned null; CPU fallback is forbidden.");
            Require(context.Backend == GRBackend.Direct3D, "Context is not Direct3D.");
            Evidence["contextBackend"] = context.Backend.ToString();
            int width = dense ? 800 : 1000;
            int height = dense ? 600 : 700;
            Stage = "external-texture";
            using ID3D12Resource texture = device.CreateCommittedResource(HeapType.Default, HeapFlags.None,
                ResourceDescription.Texture2D(Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1, 1, 0, ResourceFlags.AllowRenderTarget),
                ResourceStates.RenderTarget);
            using GRD3DTextureResourceInfo info = new()
            {
                Resource = texture.NativePointer, ResourceState = (uint)ResourceStates.RenderTarget,
                Format = (uint)Format.B8G8R8A8_UNorm, SampleCount = 1, LevelCount = 1,
            };
            using GRBackendTexture backendTexture = new(width, height, info);
            Require(backendTexture.IsValid && backendTexture.Backend == GRBackend.Direct3D, "D3D12 texture wrapper is invalid.");
            Stage = "gpu-surface";
            using SKSurface surface = SKSurface.Create(context, backendTexture, GRSurfaceOrigin.TopLeft, 0, SKColorType.Bgra8888)
                ?? throw new InvalidOperationException("Wrapping the D3D12 texture as SKSurface returned null; CPU fallback is forbidden.");
            Require(surface.Context is not null && surface.Context.Backend == GRBackend.Direct3D, "Surface has no Direct3D context.");
            Require(surface.PeekPixels() is null, "Surface exposes raster pixels.");
            using (SKImage snapshot = surface.Snapshot())
                Require(snapshot.IsTextureBacked, "Snapshot is not texture-backed.");
            Evidence["surface"] = new { width, height, Context = surface.Context!.Backend.ToString(), RasterPeek = false, SnapshotTextureBacked = true };
            Stage = "normal-plot-render";
            using ScottPlot.Plot plot = dense ? CreateDensePlot() : CreatePlot();
            for (int i = 0; i < 3; i++)
            {
                RenderPreservingCanvas(plot, surface);
                context.Flush(submit: true, synchronous: false);
                Wait(queue, fence, fenceEvent, (ulong)i + 1);
            }
            List<object> frames = new();
            for (int i = 0; i < 12; i++)
            {
                long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
                Stopwatch timer = Stopwatch.StartNew();
                RenderPreservingCanvas(plot, surface);
                context.Flush(submit: true, synchronous: false);
                double submitMs = timer.Elapsed.TotalMilliseconds;
                Wait(queue, fence, fenceEvent, (ulong)i + 4);
                double completeMs = timer.Elapsed.TotalMilliseconds;
                frames.Add(new { Frame = i, CpuRenderAndSubmitMs = submitMs, FenceWaitMs = completeMs - submitMs,
                    CompleteMs = completeMs, AllocatedBytesCurrentThread = GC.GetAllocatedBytesForCurrentThread() - bytesBefore });
                Require(surface.Context?.Backend == GRBackend.Direct3D && !context.IsAbandoned, "GPU context lost.");
            }
            device.DeviceRemovedReason.CheckError();
            Evidence["frames"] = frames;
            Evidence["workload"] = dense ? "Full Figure: 32 ordinary SignalXY x 20,000 input points, 3 bars, text, title, axes, grid, legend; 800x600; scale 1; normal Plot.Render(surface)" : "Full Figure: 2 scatter curves x 1000 points, 3 bars, text, title, axes, grid, legend; 1000x700; scale 1; normal Plot.Render(surface)";
            Evidence["measurement"] = new { Warmup = 3, Frames = 12, GpuToCpuPixelReadbacks = 0, CpuFallbacks = 0,
                FenceWaitPerFrame = true, GpuTimestamps = "not measured", NativeAllocations = "not measured", Allocations = "current rendering thread only" };
            // One explicit diagnostic readback after the render loop, never a presentation path.
            Stage = "diagnostic-readback";
            using (SKImage snapshot = surface.Snapshot())
            using (SKData png = snapshot.Encode(SKEncodedImageFormat.Png, 100))
            using (Stream stream = File.Create(Path.Combine(EvidenceDirectory, EvidenceName + ".png")))
                png.SaveTo(stream);
            Evidence["diagnosticReadbacks"] = "One PNG encode after measurement (may read GPU pixels); no per-frame readback.";
            Evidence["result"] = "PASS_OFFSCREEN_ONLY";
            Evidence["unverified"] = new[] { "all plottable/style compatibility", "WPF window", "WinUI window", "UWP window", "DPI/resize/device loss" };
            Save();
            return 0;
        }
        catch (Exception ex)
        {
            Evidence["result"] = "FAIL";
            Evidence["failureStage"] = Stage;
            Evidence["exception"] = ex.ToString();
            Save();
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>Records exact managed and native entry points in the shipped x64 package.</summary>
    private static void CheckApi()
    {
        Assembly assembly = typeof(GRContext).Assembly;
        Evidence["environment"] = new { Framework = RuntimeInformation.FrameworkDescription, OS = RuntimeInformation.OSDescription,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(), Assembly = assembly.FullName };
        Evidence["managedAssembly"] = new { assembly.Location, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(assembly.Location))) };
        Evidence["managedApi"] = new[] { typeof(GRContext), typeof(GRD3DBackendContext), typeof(GRD3DTextureResourceInfo),
            typeof(GRBackendTexture), typeof(GRBackendRenderTarget) }.ToDictionary(t => t.FullName!, t =>
            t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.Name.Contains("Direct3D") || m is ConstructorInfo || t.Name.StartsWith("GRD3D"))
                .Select(m => m.ToString()).ToArray());
        string nativePath = Path.Combine(AppContext.BaseDirectory, "runtimes/win-x64/native/libSkiaSharp.dll");
        if (!File.Exists(nativePath))
            nativePath = Path.Combine(AppContext.BaseDirectory, "libSkiaSharp.dll");
        nint library = NativeLibrary.Load(nativePath);
        try
        {
            string[] names = ["gr_direct_context_make_direct3d", "gr_direct_context_make_direct3d_with_options",
                "gr_backendtexture_new_direct3d", "gr_backendrendertarget_new_direct3d",
                "sk_surface_new_backend_texture", "sk_surface_new_backend_render_target"];
            Dictionary<string, bool> exports = names.ToDictionary(n => n, n => NativeLibrary.TryGetExport(library, n, out _));
            Evidence["nativeLibrary"] = new { Path = nativePath, Sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativePath))), Exports = exports };
            Require(exports.Values.All(x => x), "Required native export missing.");
        }
        finally { NativeLibrary.Free(library); }
        Save();
    }

    /// <summary>Selects a hardware adapter explicitly and rejects the Microsoft software rasterizer.</summary>
    private static IDXGIAdapter1 SelectHardwareAdapter(IDXGIFactory4 factory)
    {
        for (uint i = 0; factory.EnumAdapters1(i, out IDXGIAdapter1? adapter).Success; i++)
        {
            if ((adapter.Description1.Flags & AdapterFlags.Software) == 0)
                return adapter;
            adapter.Dispose();
        }
        throw new InvalidOperationException("No hardware adapter; WARP/CPU fallback is forbidden.");
    }

    /// <summary>Creates a representative full Figure through ordinary ScottPlot APIs.</summary>
    internal static ScottPlot.Plot CreatePlot()
    {
        ScottPlot.Plot plot = new();
        double[] xs = Enumerable.Range(0, 1000).Select(i => i / 100.0).ToArray();
        var line = plot.Add.Scatter(xs, xs.Select(Math.Sin).ToArray());
        line.LegendText = "Scatter sine / 1000";
        line.LineWidth = 2;
        line.MarkerSize = 0;
        var dashed = plot.Add.Scatter(xs, xs.Select(x => Math.Cos(x) * 0.6).ToArray());
        dashed.LegendText = "Dashed cosine";
        dashed.LinePattern = ScottPlot.LinePattern.Dashed;
        dashed.MarkerSize = 0;
        var bars = plot.Add.Bars(new ScottPlot.Bar[] { new ScottPlot.Bar { Position = 2, Value = 0.6 },
            new ScottPlot.Bar { Position = 4, Value = 0.8 }, new ScottPlot.Bar { Position = 6, Value = 0.4 } });
        bars.LegendText = "Bars (non-curve)";
        var text = plot.Add.Text("D3D12 GPU surface", 6, -0.6);
        text.LabelFontSize = 20;
        plot.Title("ScottPlot full Figure / SkiaSharp 3.119.0 / D3D12");
        plot.XLabel("Time (s)");
        plot.YLabel("Amplitude");
        plot.ShowLegend();
        plot.Axes.SetLimits(-1, 11, -1.5, 1.8);
        return plot;
    }

    /// <summary>Restores the caller's canvas state after the ordinary ScottPlot renderer applies DPI scaling.</summary>
    private static void RenderPreservingCanvas(ScottPlot.Plot plot, SKSurface surface)
    {
        int saveCount = surface.Canvas.Save();
        try { plot.Render(surface); }
        finally { surface.Canvas.RestoreToCount(saveCount); }
        Require(surface.Canvas.TotalMatrix == SKMatrix.Identity, "Canvas transform leaked across frames.");
    }

    /// <summary>Creates the dense comparison workload as a complete Figure using ordinary SignalXY plottables.</summary>
    private static ScottPlot.Plot CreateDensePlot()
    {
        ScottPlot.Plot plot = new();
        double[] xs = Enumerable.Range(0, 20_000).Select(i => i * 0.01).ToArray();
        for (int curve = 0; curve < 32; curve++)
        {
            int index = curve;
            double[] ys = xs.Select(x => Math.Sin(x * (1 + index % 5) + index * 0.1) + Math.Sin(x * 50 + index) * 0.2).ToArray();
            var signal = plot.Add.SignalXY(xs, ys);
            signal.LineWidth = 2;
            signal.LineStyle.AntiAlias = true;
            if (curve < 2) signal.LegendText = $"SignalXY {curve + 1} / 20,000";
        }
        var bars = plot.Add.Bars(new ScottPlot.Bar[] {
            new() { Position = 30, Value = 0.6, Size = 6 }, new() { Position = 90, Value = 0.8, Size = 6 },
            new() { Position = 150, Value = 0.4, Size = 6 } });
        bars.LegendText = "Bars";
        plot.Add.Text("Full Figure D3D12", 110, -1.3).LabelFontSize = 16;
        plot.Title("D3D12 full Figure / 32 x 20,000 / AA");
        plot.XLabel("Time (s)"); plot.YLabel("Amplitude"); plot.ShowLegend();
        plot.Axes.SetLimits(-10, 210, -1.6, 1.6);
        return plot;
    }
    /// <summary>Measures explicit completion without reading texture memory.</summary>
    private static void Wait(ID3D12CommandQueue queue, ID3D12Fence fence, ManualResetEvent wait, ulong value)
    {
        queue.Signal(fence, value).CheckError();
        if (fence.CompletedValue < value)
        {
            fence.SetEventOnCompletion(value, wait.SafeWaitHandle.DangerousGetHandle()).CheckError();
            if (!wait.WaitOne(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("D3D12 fence did not complete.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Save() => File.WriteAllText(Path.Combine(EvidenceDirectory, EvidenceName + ".json"),
        JsonSerializer.Serialize(Evidence, new JsonSerializerOptions { WriteIndented = true }));
}





