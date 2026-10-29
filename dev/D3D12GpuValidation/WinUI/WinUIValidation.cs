using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation;
using ScottPlot;
using ScottPlot.D3D12.WinUI;

/// <summary>Launches the production WinUI native GPU control for non-activating lvt acceptance.</summary>
internal static class WinUIValidation
{
    internal static string Evidence = "";
    internal static bool RunSuite;
    [STAThread] private static void Main(string[] args)
    {
        Evidence = Path.GetFullPath(args[0]); Directory.CreateDirectory(Evidence); RunSuite = args.Contains("--suite");
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(init =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _ = new WinUIValidationApp();
        });
    }
}

/// <summary>Owns one real native panel and the window-screenshot handshake.</summary>
internal sealed class WinUIValidationApp : Application
{
    private Window? Window;
    private WinUIPlotD3D12? Control;
    private WindowSuite? Suite;
    private readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    internal WinUIValidationApp() { UnhandledException += (_, args) => { File.WriteAllText(Path.Combine(WinUIValidation.Evidence, "unhandled.txt"), args.Exception.ToString()); Suite?.Fail(args.Exception); args.Handled = true; }; }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Window = new Window { Title = "D3D12 production WinUI validation" };
            Control = new WinUIPlotD3D12 { AppWindow = Window };
            Grid root = new(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
            StackPanel commands = new() { Orientation = Microsoft.UI.Xaml.Controls.Orientation.Horizontal };
            Add(commands, "Wheel action", "WheelAction", Wheel);
            Add(commands, "Resize", "ResizeProbe", () => Window.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700)));
            Add(commands, "Close", "CloseProbe", Window.Close);
            root.Children.Add(commands); Grid.SetRow(Control, 1); root.Children.Add(Control); Window.Content = root;
            Suite = new(Control, Control.CaptureBitmap, () => Control.LastGpuFrame, WinUIValidation.Evidence, "WinUI");
            Suite.Caption += caption => Window.Title = caption;
            Control.Loaded += (_, _) => { if (WinUIValidation.RunSuite) Suite.Start(); else { Control.Reset(SceneCatalog.All.First(s => s.Name.StartsWith("Performance/")).Create()); Control.Refresh(); } Timer.Start(); };
            Timer.Tick += (_, _) => { if (WinUIValidation.RunSuite) Suite.Tick(); };
            Window.Closed += (_, _) => { Timer.Stop(); Control.Dispose(); File.WriteAllText(Path.Combine(WinUIValidation.Evidence, "closed.txt"), "Production WinUI control Dispose completed."); };
            Window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 820));
            SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(Window), new nint(1), 0, 0, 0, 0, 0x13);
            Window.AppWindow.Show(false);
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(WinUIValidation.Evidence, "fatal.txt"), ex.ToString()); Exit(); }
    }
    private static void Add(Panel panel, string text, string id, Action action)
    {
        Button button = new() { Content = text, Margin = new Thickness(3) }; AutomationProperties.SetAutomationId(button, id);
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private void Wheel()
    {
        AxisLimits before = Control!.Plot.Axes.GetLimits();
        Control.UserInputProcessor.Process(new ScottPlot.Interactivity.UserActions.MouseWheelUp(Control.Plot.RenderManager.LastRender.DataRect.Center)); Control.Refresh();
        File.WriteAllText(Path.Combine(WinUIValidation.Evidence, "wheel-action.json"), JsonSerializer.Serialize(new { Before = before.ToString(), After = Control.Plot.Axes.GetLimits().ToString(), Method = "UIA Invoke -> standard UserInputProcessor MouseWheelUp" }));
    }
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
}

