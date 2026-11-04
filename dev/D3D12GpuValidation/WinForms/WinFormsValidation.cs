using System.Text.Json;
using ScottPlot;
using ScottPlot.D3D12;
using SkiaSharp;

/// <summary>Hosts the production WinForms GPU control with catalogue scenes and lifecycle probes.</summary>
internal sealed class WinFormsValidation : Form
{
    private readonly ProbeControl PlotControl = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer Timer = new() { Interval = 100 };
    private readonly WindowSuite Suite;
    private readonly string Evidence;
    private readonly bool RunSuite;
    private readonly System.Windows.Forms.Label Status = new() { AutoSize = true };
    private int HandleGeneration;
    private bool Animate;
    private long LastDiagnostics;

    [STAThread]
    private static void Main(string[] args)
    {
        string evidence = Path.GetFullPath(args.FirstOrDefault(x => !x.StartsWith("--")) ?? "artifacts/winforms-d3d12/manual");
        Directory.CreateDirectory(evidence);
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Application.Run(new WinFormsValidation(evidence, args.Contains("--suite"), SceneCatalog.Select(args)));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(evidence, "fatal.txt"), ex.ToString());
            Environment.ExitCode = 1;
        }
    }

    private WinFormsValidation(string evidence, bool suite, IReadOnlyList<SceneDefinition> scenes)
    {
        Evidence = evidence;
        RunSuite = suite;
        Text = "WinForms D3D12 validation";
        ClientSize = new System.Drawing.Size(1000, 740);
        FlowLayoutPanel commands = new() { Dock = DockStyle.Top, AutoSize = true };
        Add(commands, "Wheel action", () =>
        {
            AxisLimits before = PlotControl.Plot.Axes.GetLimits();
            PlotControl.UserInputProcessor.Process(new ScottPlot.Interactivity.UserActions.MouseWheelUp(PlotControl.Plot.RenderManager.LastRender.DataRect.Center));
            PlotControl.Refresh();
            File.WriteAllText(Path.Combine(Evidence, "wheel-action.json"), JsonSerializer.Serialize(new { Before = before.ToString(), After = PlotControl.Plot.Axes.GetLimits().ToString(), Method = "Programmatic UserInputProcessor action; verify real mouse separately" }));
        });
        Add(commands, "Resize", () => ClientSize = ClientSize.Width == 1000 ? new(800, 600) : new(1000, 740));
        Add(commands, "Recreate handle", () => { PlotControl.RecreateWindow(); HandleGeneration++; PlotControl.Refresh(); });
        Add(commands, "Worker refresh", () => { _ = Task.Run(() => { for (int i = 0; i < 1000; i++) PlotControl.Refresh(); }); });
        Add(commands, "Animate", () => { Animate = !Animate; Timer.Interval = Animate ? 16 : 100; });
        Add(commands, "Reset", () => { PlotControl.Reset(scenes[0].Create()); PlotControl.Refresh(); });
        Add(commands, "Multiplot", () =>
        {
            Plot second = PlotControl.Multiplot.AddPlot();
            second.Add.Bars(new double[] { 1, 3, 2 });
            second.Title("Second subplot");
            PlotControl.Refresh();
        });
        Add(commands, "Capture", () =>
        {
            using SKBitmap bitmap = PlotControl.CaptureBitmap();
            using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(Evidence, "manual-gpu.png"), png.ToArray());
        });
        Add(commands, "Close", Close);
        commands.Controls.Add(Status);
        Controls.Add(PlotControl);
        Controls.Add(commands);
        Suite = new(PlotControl, PlotControl.CaptureBitmap, () => PlotControl.LastGpuFrame, evidence, "WinForms", scenes);
        Suite.Caption += caption => Text = caption;
        Shown += (_, _) =>
        {
            if (RunSuite) Suite.Start();
            else
            {
                PlotControl.Reset(scenes.FirstOrDefault(s => s.Name.StartsWith("Performance/"))?.Create() ?? scenes[0].Create());
                PlotControl.Refresh();
            }
            Timer.Start();
        };
        Timer.Tick += (_, _) =>
        {
            if (RunSuite) Suite.Tick();
            else if (Animate) PlotControl.Refresh();
            if (PlotControl.PresentedFrames == 0) return;
            if (Environment.TickCount64 - LastDiagnostics < 1000) return;
            LastDiagnostics = Environment.TickCount64;
            Status.Text = $"Frames={PlotControl.PresentedFrames} Readbacks={PlotControl.PixelReadbackCount} DPI={PlotControl.DeviceDpi}";
            File.WriteAllText(Path.Combine(Evidence, "diagnostics.json"), JsonSerializer.Serialize(new
            {
                Backend = PlotControl.GRContext.Backend.ToString(),
                HandleGeneration, PlotControl.PresentedFrames, PlotControl.PixelReadbackCount,
                PlotControl.SwapChainResizeCount, PlotControl.DisplayScale, PlotControl.DeviceDpi,
                Width = PlotControl.ClientSize.Width, Height = PlotControl.ClientSize.Height,
                Frame = PlotControl.LastGpuFrame, RenderCount = PlotControl.Plot.RenderManager.RenderCount,
            }, new JsonSerializerOptions { WriteIndented = true }));
        };
        FormClosed += (_, _) =>
        {
            Timer.Stop();
            Timer.Dispose();
            PlotControl.Dispose();
            File.WriteAllText(Path.Combine(Evidence, "closed.txt"), "Production WinForms GPU control disposed on the owning thread.");
        };
    }

    private static void Add(FlowLayoutPanel panel, string text, Action action)
    {
        Button button = new() { Text = text, AutoSize = true, AccessibleName = text };
        button.Click += (_, _) => action();
        panel.Controls.Add(button);
    }

    private sealed class ProbeControl : FormsPlotD3D12
    {
        internal void RecreateWindow() => RecreateHandle();
    }
}
