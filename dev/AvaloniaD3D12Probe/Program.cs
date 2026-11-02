using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ScottPlot.Avalonia;

namespace AvaloniaD3D12Probe;

/// <summary>Starts the isolated Windows GPU presentation and benchmark application.</summary>
internal static class Program
{
    internal static string[] Arguments = [];
    internal static string Output = "";

    [STAThread]
    private static void Main(string[] args)
    {
        Arguments = args;
        Output = Path.GetFullPath(Option("--output") ?? "../../artifacts/avalonia-d3d12-probe/latest");
        Directory.CreateDirectory(Output);
        try
        {
            AppBuilder.Configure<ProbeApp>().UsePlatformDetect()
                .With(new Win32PlatformOptions { RenderingMode = [Win32RenderingMode.AngleEgl] })
                .LogToTrace().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(Output, "fatal.txt"), error.ToString());
            Environment.ExitCode = 1;
        }
    }

    internal static string? Option(string name)
    {
        int index = Array.IndexOf(Arguments, name);
        return index >= 0 && index + 1 < Arguments.Length ? Arguments[index + 1] : null;
    }
}

/// <summary>Creates the probe's real Avalonia desktop window.</summary>
internal sealed class ProbeApp : Application
{
    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new ProbeWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Displays one measured chart at a time, plus controls for manual window verification.</summary>
internal sealed class ProbeWindow : Window
{
    internal readonly AvaPlot Baseline = new();
    internal readonly GpuPlot Gpu = new();
    internal readonly FigureFixture BaselineFigure;
    internal readonly FigureFixture GpuFigure;
    internal readonly BackendWitness Witness = new() { Width = 8, Height = 8, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    internal readonly Border Viewport = new() { Width = 800, Height = 600 };
    private readonly ContentControl Chart = new();
    private readonly TextBlock Status = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 880 };
    private readonly ProbeRunner Runner;
    private readonly StackPanel Buttons = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    internal bool IsGpu { get; private set; } = true;
    private bool Busy;
    private bool ClosingAfterDispose;

    internal ProbeWindow()
    {
        Title = "ScottPlot Avalonia D3D12 Probe";
        SizeToContent = SizeToContent.WidthAndHeight;
        BaselineFigure = new(Baseline.Plot); GpuFigure = new(Gpu.Plot);
        Grid chartLayer = new();
        chartLayer.Children.Add(Chart); chartLayer.Children.Add(Witness);
        chartLayer.Children.Add(new Border
        {
            Background = Brushes.LightGoldenrodYellow, Padding = new Thickness(6), IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock { Text = "Avalonia overlay", Foreground = Brushes.Black },
        });
        Viewport.Child = chartLayer;
        Grid root = new() { Margin = new Thickness(12), RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8 };
        Grid.SetRow(Status, 1); Grid.SetRow(Viewport, 2);
        root.Children.Add(Buttons); root.Children.Add(Status); root.Children.Add(Viewport); Content = root;
        Runner = new(this);
        AddButton("AvaPlot", () => SelectAsync(false, false));
        AddButton("D3D12", () => SelectAsync(true, false));
        AddButton("D3D12 optimized", () => SelectAsync(true, true));
        AddButton("Validate", Runner.ValidateAsync);
        AddButton("Benchmark", Runner.BenchmarkAsync);
        AddButton("Save evidence", () => Runner.SnapshotAsync("manual"));
        Chart.Content = Gpu;
        Gpu.FramePresented += () => { if (!Busy) Runner.SaveInteraction(); };
        ScalingChanged += (_, _) => Gpu.Refresh();
        Opened += async (_, _) => await ExecuteAsync(async () =>
        {
            await Task.Delay(500);
            await SelectAsync(true, false);
            Runner.SaveEnvironment();
            if (Program.Option("--run") is { } run)
            {
                if (run is "smoke" or "suite") await Runner.ValidateAsync();
                if (run is "benchmark" or "suite") await Runner.BenchmarkAsync();
                SetStatus("Completed. Wheel/drag the plot, resize the window, or select a backend.");
                await Runner.SnapshotAsync("completed");
                File.WriteAllText(Path.Combine(Program.Output, "completed.txt"), "complete");
            }
        });
        Closing += async (_, e) =>
        {
            if (ClosingAfterDispose) return;
            e.Cancel = true;
            await Gpu.DisposeAsync();
            Baseline.Plot.Dispose(); Gpu.Plot.Dispose();
            ClosingAfterDispose = true; Close();
        };
    }

    private void AddButton(string caption, Func<Task> action)
    {
        Button button = new() { Content = caption };
        button.Click += async (_, _) => await ExecuteAsync(action);
        Buttons.Children.Add(button);
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        if (Busy) return;
        Busy = true; Buttons.IsEnabled = false;
        try { await action(); }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(Program.Output, "failure.txt"), error.ToString());
            SetStatus("FAILED: " + error.Message);
        }
        finally
        {
            Busy = false; Buttons.IsEnabled = true;
            double width = Bounds.Width, height = Bounds.Height;
            SizeToContent = SizeToContent.Manual;
            Width = width; Height = height;
            Viewport.Width = double.NaN; Viewport.Height = double.NaN;
        }
    }

    internal void SetStatus(string text)
    {
        Status.Text = text;
        File.AppendAllText(Path.Combine(Program.Output, "progress.log"), $"{DateTimeOffset.Now:O} {text}{Environment.NewLine}");
    }

    internal async Task SelectAsync(bool gpu, bool optimize)
    {
        Gpu.AutomaticRefresh = false;
        IsGpu = gpu; Gpu.OptimizeSignals = optimize;
        Chart.Content = gpu ? Gpu : Baseline;
        UpdateLayout();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        if (gpu) await Gpu.RenderFrameAsync(); else Baseline.InvalidateVisual();
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(TimeSpan.FromSeconds(20));
        if (Witness.Backend is null) throw new InvalidOperationException("Avalonia hardware canvas witness did not render.");
        SetStatus($"{(gpu ? optimize ? "D3D12 optimized" : "D3D12" : "Existing AvaPlot")} | Avalonia {Witness.Backend} | DPI {RenderScaling:0.##} | {Gpu.AdapterName}");
        Gpu.AutomaticRefresh = gpu;
    }

    internal Compositor Compositor => ElementComposition.GetElementVisual(Viewport)!.Compositor;
}
