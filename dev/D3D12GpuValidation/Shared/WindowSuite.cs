using System.IO;
using System.Text.Json;
using ScottPlot;
using ScottPlot.D3D12.Rendering;
using SkiaSharp;

/// <summary>Coordinates real-window scenes with an external lvt screenshot acknowledgement.</summary>
internal sealed class WindowSuite
{
    private readonly IPlotControl Control;
    private readonly Func<SKBitmap> Capture;
    private readonly Func<D3D12FrameStatistics> Statistics;
    private readonly string Evidence;
    private readonly string Platform;
    private readonly IReadOnlyList<SceneDefinition> Scenes;
    private readonly List<object> Cases = [];
    private readonly HashSet<string> Covered = [];
    private int Index = -1;
    private int Phase;
    private long LoadedAt;
    private string? Error;
    internal event Action<string>? Caption;
    internal bool Completed { get; private set; }
    internal WindowSuite(IPlotControl control, Func<SKBitmap> capture, Func<D3D12FrameStatistics> statistics, string evidence, string platform, IReadOnlyList<SceneDefinition>? scenes = null)
    {
        Control = control; Capture = capture; Statistics = statistics; Evidence = evidence; Platform = platform; Scenes = scenes ?? SceneCatalog.All;
        Directory.CreateDirectory(Evidence);
    }
    internal void Start() { if (Index < 0) Next(); }
    internal void Fail(Exception exception) { Error = exception.ToString(); Phase = 0; }
    internal void Tick()
    {
        if (Completed || Index < 0) return;
        try { TickCore(); }
        catch (Exception ex) { Fail(ex); }
    }
    private void TickCore()
    {
        if (Completed || Index < 0) return;
        if (Phase == 0)
        {
            if (Error is null && Control.Plot.RenderManager.RenderCount == 0)
            {
                if (Environment.TickCount64 - LoadedAt > 20000) Error = "No completed Figure within20s.";
                else return;
            }
            if (Environment.TickCount64 - LoadedAt < 180) return;
            SceneDefinition scene = Scenes[Index];
            string name = Index.ToString("D4");
            string? currentError = Error;
            if (currentError is null)
            {
                try
                {
                    using SKBitmap bitmap = Capture();
                    using SKData encoded = bitmap.Encode(SKEncodedImageFormat.Png, 100);
                    File.WriteAllBytes(Path.Combine(Evidence, name + "-gpu.png"), encoded.ToArray());
                    foreach (IPlottable item in Control.Plot.PlottableList) Covered.Add(SceneCatalog.CanonicalType(item.GetType()));
                }
                catch (Exception ex) { currentError = ex.ToString(); }
            }
            Cases.Add(new { Index, scene.Name, scene.Capability, scene.AuxiliaryRasterHitMap,
                Result = currentError is null ? "REAL_WINDOW_GPU_RENDER_PASS" : "FAIL", Error = currentError,
                Control.DisplayScale, Frame = Statistics(), Plottables = Control.Plot.PlottableList.Select(p => SceneCatalog.CanonicalType(p.GetType())).Distinct().ToArray() });
            Save();
            AtomicWrite(Path.Combine(Evidence, "scene-ready.json"), JsonSerializer.Serialize(new { Index, scene.Name, Output = name + "-lvt.png", Platform, Error = currentError }));
            Phase = 1;
        }
        else if (File.Exists(Path.Combine(Evidence, "capture-ack.txt")) && int.TryParse(File.ReadAllText(Path.Combine(Evidence, "capture-ack.txt")), out int acknowledged) && acknowledged == Index)
            Next();
    }
    private void Next()
    {
        if (++Index >= Scenes.Count) { Completed = true; Save(); AtomicWrite(Path.Combine(Evidence, "suite-completed.txt"), "complete"); return; }
        Error = null; Phase = 0; LoadedAt = Environment.TickCount64;
        SceneDefinition scene = Scenes[Index];
        Caption?.Invoke($"{Platform} [{Index + 1}/{Scenes.Count}] {scene.Name}");
        try { Control.Reset(scene.Create()); Control.Refresh(); }
        catch (Exception ex) { Error = ex.ToString(); }
    }
    private void Save()
    {
        string[] inventory = typeof(Plot).Assembly.GetTypes().Where(t => typeof(IPlottable).IsAssignableFrom(t) && !t.IsAbstract).Select(SceneCatalog.CanonicalType).Distinct().Order().ToArray();
        AtomicWrite(Path.Combine(Evidence, "window-suite.json"), JsonSerializer.Serialize(new { Platform, Completed,
            Scenes = Cases.Count, Expected = Scenes.Count, Backend = Control.GRContext?.Backend.ToString(),
            NativeMsaaSamples = 4, ExplicitGpuResolve = true, PerRenderReadbacks = 0, DiagnosticReadbacks = Cases.Count,
            Inventory = inventory.Select(t => new { Type = t, Covered = Covered.Contains(t) }).ToArray(),
            Cases }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static void AtomicWrite(string path, string text)
    {
        File.WriteAllText(path + ".tmp", text);
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(path + ".tmp", path, true); return; }
            catch (IOException) when (attempt < 20) { Thread.Sleep(50); }
        }
    }
}

