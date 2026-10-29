using System.Runtime.InteropServices;
using System.Text.Json;
using Windows.ApplicationModel.Activation;
using Windows.Storage;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Automation;
using ScottPlot;
using ScottPlot.D3D12.UWP;

/// <summary>Starts a real UWP CoreCLR Application and its native Windows.UI.Xaml GPU control.</summary>
internal static class UwpValidation
{
    internal static string Evidence => Path.Combine(ApplicationData.Current.LocalFolder.Path, "validation");
    [MTAThread] private static void Main()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(init => { _ = new UwpValidationApp(); });
    }
}

/// <summary>Owns the actual AppContainer window and catalogue handshake.</summary>
internal sealed class UwpValidationApp : Application
{
    private UwpPlotD3D12? Control;
    private WindowSuite? Suite;
    private readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private bool RunSuite;
    internal UwpValidationApp()
    {
        Directory.CreateDirectory(UwpValidation.Evidence);
        UnhandledException += (_, args) => { File.WriteAllText(Path.Combine(UwpValidation.Evidence, "unhandled.txt"), args.Exception.ToString()); Suite?.Fail(args.Exception); args.Handled = true; };
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            RunSuite = args.Arguments.Contains("--suite");
            Control = new UwpPlotD3D12();
            Grid root = new(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new());
            StackPanel commands = new() { Orientation = Windows.UI.Xaml.Controls.Orientation.Horizontal };
            Add(commands, "Wheel action", "WheelAction", Wheel);
            Add(commands, "Resize", "ResizeProbe", () => Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().TryResizeView(new Windows.Foundation.Size(800, 600)));
            Add(commands, "Close", "CloseProbe", Exit);
            root.Children.Add(commands); Grid.SetRow(Control, 1); root.Children.Add(Control); Window.Current.Content = root;
            Suite = new(Control, Control.CaptureBitmap, () => Control.LastGpuFrame, UwpValidation.Evidence, "UWP");
            Suite.Caption += caption => Windows.UI.ViewManagement.ApplicationView.GetForCurrentView().Title = caption;
            Control.Loaded += (_, _) => { if (RunSuite) Suite.Start(); else { Control.Reset(SceneCatalog.All.First(s => s.Name.StartsWith("Performance/")).Create()); Control.Refresh(); } Timer.Start(); };
            Timer.Tick += (_, _) => { if (RunSuite) Suite.Tick(); };
            Window.Current.Closed += (_, _) => { Timer.Stop(); Control.Dispose(); File.WriteAllText(Path.Combine(UwpValidation.Evidence, "closed.txt"), "Production UWP control Dispose completed."); };
            Windows.UI.ViewManagement.ApplicationView.PreferredLaunchViewSize = new Windows.Foundation.Size(900, 680);
            Windows.UI.ViewManagement.ApplicationView.PreferredLaunchWindowingMode = Windows.UI.ViewManagement.ApplicationViewWindowingMode.PreferredLaunchViewSize;
            Window.Current.Activate();
            File.WriteAllText(Path.Combine(UwpValidation.Evidence, "process.json"), JsonSerializer.Serialize(new { Pid = Environment.ProcessId, CoreWindow = true, Runtime = Environment.Version.ToString(), Args = args.Arguments }));
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(UwpValidation.Evidence, "fatal.txt"), ex.ToString()); Exit(); }
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
        File.WriteAllText(Path.Combine(UwpValidation.Evidence, "wheel-action.json"), JsonSerializer.Serialize(new { Before = before.ToString(), After = Control.Plot.Axes.GetLimits().ToString(), Method = "UIA Invoke -> standard UserInputProcessor MouseWheelUp" }));
    }
}

