using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using ScottPlot;
using ScottPlot.D3D12;

/// <summary>Hosts the production WPF GPU control for background lvt visual and input verification.</summary>
internal sealed class WpfValidation : Window
{
    private readonly WpfPlotD3D12 Control = new();
    private readonly WindowSuite Suite;
    private readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly string Evidence;
    [STAThread] private static void Main(string[] args)
    {
        string evidence = Path.GetFullPath(args[0]); Directory.CreateDirectory(evidence);
        try { new Application().Run(new WpfValidation(evidence, args.Contains("--suite"), SceneCatalog.Select(args))); }
        catch (Exception ex) { File.WriteAllText(Path.Combine(evidence, "fatal.txt"), ex.ToString()); Environment.ExitCode = 1; }
    }
    private WpfValidation(string evidence, bool suite, IReadOnlyList<SceneDefinition> scenes)
    {
        Evidence = evidence; ShowActivated = false; Width = 1000; Height = 740; Left = Top = 0; WindowStartupLocation = WindowStartupLocation.Manual;
        DockPanel root = new(); StackPanel commands = new() { Orientation = System.Windows.Controls.Orientation.Horizontal };
        Add(commands, "Wheel action", "WheelAction", Wheel);
        Add(commands, "Resize", "ResizeProbe", () => { Width = Width == 1000 ? 850 : 1000; Height = Height == 740 ? 650 : 740; });
        Add(commands, "Close", "CloseProbe", Close);
        DockPanel.SetDock(commands, Dock.Top); root.Children.Add(commands); root.Children.Add(Control); Content = root;
        Suite = new(Control, Control.CaptureBitmap, () => Control.LastGpuFrame, evidence, "WPF", scenes);
        Suite.Caption += caption => Title = caption;
        SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(this).Handle, new nint(1), 0, 0, 0, 0, 0x13);
        Loaded += (_, _) => { if (suite) Suite.Start(); else { Control.Reset(SceneCatalog.All.First(s => s.Name.StartsWith("Performance/")).Create()); Control.Refresh(); } Timer.Start(); };
        Timer.Tick += (_, _) => { if (suite) Suite.Tick(); };
        Closed += (_, _) => { Timer.Stop(); Control.Dispose(); File.WriteAllText(Path.Combine(Evidence, "closed.txt"), "Production WPF control Dispose completed; Intel WPF memory acceptance skipped."); };
        Dispatcher.UnhandledException += (_, args) => { Suite.Fail(args.Exception); args.Handled = true; };
    }
    private static void Add(Panel panel, string text, string id, Action action)
    {
        Button button = new() { Content = text, Margin = new Thickness(3) }; AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private void Wheel()
    {
        AxisLimits before = Control.Plot.Axes.GetLimits();
        Control.UserInputProcessor.Process(new ScottPlot.Interactivity.UserActions.MouseWheelUp(Control.Plot.RenderManager.LastRender.DataRect.Center));
        Control.Refresh();
        File.WriteAllText(Path.Combine(Evidence, "wheel-action.json"), JsonSerializer.Serialize(new { Before = before.ToString(), After = Control.Plot.Axes.GetLimits().ToString(), Method = "UIA Invoke -> standard UserInputProcessor MouseWheelUp" }));
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}

