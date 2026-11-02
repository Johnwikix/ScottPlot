using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ScottPlot;
using ScottPlot.Avalonia;
using ScottPlot.Avalonia.D3D12;
using ScottPlot.D3D12.Rendering;

namespace AvaloniaBackendValidation;

/// <summary>Runs the packaged unified AvaPlot control in a real Windows window.</summary>
internal static class Program
{
    internal static string[] Arguments = [];
    internal static string Output = "";
    internal static bool Has(string argument) => Arguments.Contains(argument);

    [STAThread]
    private static void Main(string[] args)
    {
        Arguments = args;
        int outputIndex = Array.IndexOf(args, "--output");
        Output = Path.GetFullPath(outputIndex >= 0 ? args[outputIndex + 1] : "../../artifacts/avalonia-backend/latest");
        Directory.CreateDirectory(Output);
        try
        {
            var builder = AppBuilder.Configure<ValidationApp>().UsePlatformDetect()
                .With(new Win32PlatformOptions { RenderingMode = [Has("--software") ? Win32RenderingMode.Software : Win32RenderingMode.AngleEgl] });
            if (!Has("--no-provider")) builder.UseScottPlotD3D12();
            builder.StartWithClassicDesktopLifetime(args);
        }
        catch (Exception error) { File.WriteAllText(Path.Combine(Output, "fatal.txt"), error.ToString()); }
    }

    internal static void Save(string name, object value) => File.WriteAllText(Path.Combine(Output, name),
        JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>Creates the validation application.</summary>
internal sealed class ValidationApp : Application
{
    /// <inheritdoc />
    public override void Initialize() => Styles.Add(new FluentTheme());
    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.MainWindow = new ValidationWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Validates backend selection, DPI, resource ownership and explicit failures through the public API.</summary>
internal sealed class ValidationWindow : Window
{
    private readonly AvaPlot Control = new();
    private readonly ContentControl Host = new();
    private readonly Border Viewport = new() { Width = 800, Height = 600 };
    private readonly TextBlock Status = new() { MaxWidth = 880, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel Buttons = new() { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 6 };
    private readonly List<object> Checks = [];
    private bool Busy, ClosingAfterDispose;
    private int ErrorEvents;

    internal ValidationWindow()
    {
        Title = "ScottPlot Avalonia Optional Backend Validation";
        SizeToContent = SizeToContent.WidthAndHeight;
        Host.Content = Control;
        Grid layer = new();
        layer.Children.Add(Host);
        layer.Children.Add(new Border
        {
            Background = Brushes.LightGoldenrodYellow, IsHitTestVisible = false, Padding = new(6),
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Bottom,
            Child = new TextBlock { Text = "Avalonia overlay", Foreground = Brushes.Black },
        });
        Viewport.Child = layer;
        Grid root = new() { Margin = new(12), RowDefinitions = new("Auto,Auto,*"), RowSpacing = 8 };
        Grid.SetRow(Status, 1); Grid.SetRow(Viewport, 2);
        root.Children.Add(Buttons); root.Children.Add(Status); root.Children.Add(Viewport); Content = root;
        _ = new AvaloniaD3D12Probe.FigureFixture(Control.Plot);
        Control.RenderFailed += (_, e) =>
        {
            ErrorEvents++;
            Program.Save("last-error.json", new { Backend = e.Backend.ToString(), Error = e.Exception.ToString(), ErrorEvents });
        };
        AddButton("Skia", () => SelectAsync(AvaPlotRenderBackend.PlatformSkia));
        AddButton("D3D12", () => SelectAsync(AvaPlotRenderBackend.Direct3D12));
        AddButton("Validate", ValidateAsync);
        AddButton("Inject failure", InjectRuntimeFailureAsync);
        AddButton("Retry", Control.RetryRenderAsync);
        AddButton("Save", () => SnapshotAsync("manual"));
        Opened += async (_, _) => await ExecuteAsync(async () =>
        {
            await Task.Delay(350);
            await Control.RefreshAsync();
            Program.Save("environment.json", new
            {
                OS = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                Avalonia = typeof(Application).Assembly.GetName().Version?.ToString(),
                DefaultBackend = Control.RenderBackend.ToString(),
                Software = Program.Has("--software"), Provider = !Program.Has("--no-provider"),
                RenderScaling, Screens = Screens.All.Select(s => new { s.Scaling, s.Bounds.Width, s.Bounds.Height }),
            });
            if (Program.Has("--software") || Program.Has("--no-provider"))
            {
                await ValidateUnavailableAsync();
                File.WriteAllText(Path.Combine(Program.Output, "completed.txt"), "expected failure verified");
            }
            else if (Program.Has("--run"))
            {
                await ValidateAsync();
                File.WriteAllText(Path.Combine(Program.Output, "completed.txt"), "complete");
            }
        });
        Closing += async (_, e) =>
        {
            if (ClosingAfterDispose) return;
            e.Cancel = true;
            await Control.DisposeAsync();
            ClosingAfterDispose = true;
            Program.Save("shutdown.json", new { Disposed = true });
            Close();
        };
    }

    private void AddButton(string caption, Func<Task> action)
    {
        Button button = new() { Content = caption };
        global::Avalonia.Automation.AutomationProperties.SetAutomationId(button, caption.Replace(" ", ""));
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
            SizeToContent = SizeToContent.Manual; Width = width; Height = height;
            Viewport.Width = double.NaN; Viewport.Height = double.NaN;
        }
    }

    private void SetStatus(string message)
    {
        Status.Text = message;
        File.AppendAllText(Path.Combine(Program.Output, "progress.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
    }

    private async Task SelectAsync(AvaPlotRenderBackend backend)
    {
        Control.RenderBackend = backend;
        await Control.RefreshAsync();
        SetStatus($"{backend} | logical {Control.Bounds.Width:0.##} x {Control.Bounds.Height:0.##} | DPI {RenderScaling} | Plot.ScaleFactor {Control.Plot.ScaleFactor}");
        SaveState();
    }

    private void SaveState()
    {
        var gpu = Control.RenderBackendInstance as D3D12AvaPlotRenderer;
        Program.Save("state.json", new
        {
            Backend = Control.RenderBackend.ToString(), Error = Control.RenderException?.ToString(),
            ErrorEvents, RenderScaling, Control.DisplayScale, Control.Plot.ScaleFactor,
            Width = Control.Bounds.Width, Height = Control.Bounds.Height,
            FigureWidth = Control.Plot.LastRender.FigureRect.Width, FigureHeight = Control.Plot.LastRender.FigureRect.Height,
            GpuBackend = gpu?.GRContext?.Backend.ToString(), gpu?.AdapterName, gpu?.CompletedFrames,
            gpu?.PixelReadbackCount, gpu?.PresentationCopies, gpu?.SharedTextureAllocations,
            gpu?.LastFrame, gpu?.CapacityWidth, gpu?.CapacityHeight,
        });
    }

    private async Task SnapshotAsync(string name)
    {
        Compositor compositor = ElementComposition.GetElementVisual(this)!.Compositor;
        await compositor.RequestCompositionBatchCommitAsync().Rendered.WaitAsync(TimeSpan.FromSeconds(20));
        using var image = await compositor.CreateCompositionVisualSnapshot(ElementComposition.GetElementVisual(this)!, RenderScaling);
        image.Save(Path.Combine(Program.Output, name + ".png"));
        SaveState();
    }

    private async Task ValidateAsync()
    {
        Checks.Clear();
        SizeToContent = SizeToContent.WidthAndHeight;
        Viewport.Width = 800; Viewport.Height = 600; UpdateLayout();
        SetStatus("Validating the unified AvaPlot control");
        Control.Reset(new Plot());
        _ = new AvaloniaD3D12Probe.FigureFixture(Control.Plot);
        Control.Plot.ScaleFactor = 1.4;
        Control.Plot.Layout.Fixed(new PixelPadding(50));
        await SelectAsync(AvaPlotRenderBackend.PlatformSkia);
        Pixel referencePixel = Control.Plot.GetPixel(new Coordinates(100, .3));
        await SnapshotAsync("skia");
        await SelectAsync(AvaPlotRenderBackend.Direct3D12);
        Pixel gpuPixel = Control.Plot.GetPixel(new Coordinates(100, .3));
        Require(Math.Abs(referencePixel.X - gpuPixel.X) < .01 && Math.Abs(referencePixel.Y - gpuPixel.Y) < .01,
            "Logical coordinates changed when switching backends");
        Check("same-logical-api", new { referencePixel.X, referencePixel.Y, GpuX = gpuPixel.X, GpuY = gpuPixel.Y });
        await SnapshotAsync("d3d12");
        for (int cycle = 0; cycle < 3; cycle++)
        {
            foreach ((int width, int height) in new[] { (640, 420), (960, 540), (321, 257), (800, 600) })
            {
                Viewport.Width = width; Viewport.Height = height; UpdateLayout();
                await Control.RefreshAsync();
                ValidateDimensions();
                Check("resize", new { Cycle = cycle, Width = width, Height = height });
            }
        }
        PixelPoint originalPosition = Position;
        foreach (var screen in Screens.All)
        {
            Position = new(screen.WorkingArea.X + 30, screen.WorkingArea.Y + 30);
            await Task.Delay(350); await Control.RefreshAsync();
            Require(Math.Abs(RenderScaling - screen.Scaling) < .001, "Actual monitor DPI mismatch");
            ValidateDimensions();
            var point = new Pixel((float)(Control.Bounds.Width * .55), (float)(Control.Bounds.Height * .55));
            Coordinates before = Control.Plot.GetCoordinates(point);
            double span = Control.Plot.Axes.GetLimits().HorizontalSpan;
            Control.UserInputProcessor.Process(new ScottPlot.Interactivity.UserActions.MouseWheelUp(point));
            await Control.RefreshAsync();
            Coordinates after = Control.Plot.GetCoordinates(point);
            Require(Math.Abs(before.X - after.X) < 1e-6 && Math.Abs(before.Y - after.Y) < 1e-6 &&
                Control.Plot.Axes.GetLimits().HorizontalSpan < span, "Logical zoom anchor changed after DPI scaling");
            Check("actual-dpi-and-zoom", new { Expected = screen.Scaling, Actual = RenderScaling, BeforeX = before.X, AfterX = after.X });
            await SnapshotAsync("monitor-" + screen.Scaling.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
        }
        Position = originalPosition; await Task.Delay(350);
        for (int cycle = 0; cycle < 4; cycle++)
        {
            var previous = Control.RenderBackendInstance;
            Host.Content = null;
            Host.Content = Control; UpdateLayout();
            await Control.RefreshAsync();
            Require(!ReferenceEquals(previous, Control.RenderBackendInstance), "Detach/reload retained the disposed renderer");
            Check("detach-reload", new { Cycle = cycle });
            await SelectAsync(AvaPlotRenderBackend.PlatformSkia);
            await SelectAsync(AvaPlotRenderBackend.Direct3D12);
        }
        await ValidateInitializationFailureAsync();
        await InjectRuntimeFailureAsync();
        Require(Control.RenderException is not null, "Runtime failure was not latched");
        await Control.RetryRenderAsync();
        Require(Control.RenderException is null, "Explicit runtime retry did not clear the error");
        Check("runtime-retry", new { ErrorEvents });

        Plot replacement = new();
        replacement.Add.Signal(new double[] { 1, 4, 2 });
        replacement.ScaleFactor = 1.4;
        Control.Reset(replacement);
        await Control.RefreshAsync();
        Require(ReferenceEquals(Control.Plot.PlotControl, Control), "Reset did not attach the replacement model");
        var multi = new Multiplot(Control.Plot);
        Plot lower = multi.AddPlot(); lower.Add.Bars(new double[] { 1, 3, 2 }); lower.PlotControl = Control;
        Control.Multiplot = multi;
        await Control.RefreshAsync();
        Require(lower.RenderManager.RenderCount > 0, "Multiplot did not render every subplot");
        Check("reset-and-multiplot", new { Subplots = multi.Subplots.Count });
        await SnapshotAsync("multiplot");
        var gpu = (D3D12AvaPlotRenderer)Control.RenderBackendInstance!;
        Require(gpu.PixelReadbackCount == 0, "Normal presentation performed a pixel readback");
        for (int frame = 0; frame < 30; frame++) await Control.RefreshAsync();
        Require(gpu.PixelReadbackCount == 0, "Repeated presentation read pixels back");
        Check("no-per-frame-readback", new { gpu.CompletedFrames, gpu.PresentationCopies, gpu.PixelReadbackCount });
        using (var capture = gpu.CaptureBitmap())
        using (var encoded = capture.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100))
            File.WriteAllBytes(Path.Combine(Program.Output, "explicit-gpu-export.png"), encoded.ToArray());
        Require(gpu.PixelReadbackCount == 1, "Explicit export readback counter mismatch");
        Check("explicit-export", new { gpu.PixelReadbackCount });
        await SnapshotAsync("completed");
        Program.Save("validation.json", new { Pass = true, Checks, ErrorEvents });
        SetStatus("PASS: unified API, DPI/zoom, resize, detach/reload, strict failures, explicit retry and GPU export");
    }

    private void ValidateDimensions()
    {
        var gpu = (D3D12AvaPlotRenderer)Control.RenderBackendInstance!;
        Require(gpu.LastFrame.Width == (int)Math.Ceiling(Control.Bounds.Width * RenderScaling) &&
            gpu.LastFrame.Height == (int)Math.Ceiling(Control.Bounds.Height * RenderScaling), "Physical output does not match the window DPI");
        Require(Math.Abs(Control.Plot.ScaleFactor - 1.4) < 1e-6, "DPI rendering overwrote the user ScaleFactor");
        Require(Math.Abs(Control.Plot.LastRender.FigureRect.Width - Control.Bounds.Width) < .01, "Model coordinates are not logical pixels");
    }

    private async Task ValidateInitializationFailureAsync()
    {
        await SelectAsync(AvaPlotRenderBackend.PlatformSkia);
        Exception failure = new PlatformNotSupportedException("Injected D3D12 initialization failure");
        using (AvaPlotRenderBackendRegistry.RegisterDirect3D12(() => throw failure))
        {
            int before = Control.Plot.RenderManager.RenderCount;
            Control.RenderBackend = AvaPlotRenderBackend.Direct3D12;
            try { await Control.RefreshAsync(); } catch { }
            Require(ReferenceEquals(Control.RenderException, failure) && Control.RenderBackend == AvaPlotRenderBackend.Direct3D12 &&
                Control.Plot.RenderManager.RenderCount == before, "Initialization failure caused a fallback or lost its original exception");
            Check("initialization-failure", new { Backend = Control.RenderBackend.ToString(), Error = Control.RenderException!.Message });
            await SnapshotAsync("initialization-failure");
        }
        await Control.RetryRenderAsync();
        Require(Control.RenderException is null && Control.RenderBackendInstance is D3D12AvaPlotRenderer, "Initialization retry failed");
    }

    private async Task InjectRuntimeFailureAsync()
    {
        if (Control.RenderBackend != AvaPlotRenderBackend.Direct3D12) await SelectAsync(AvaPlotRenderBackend.Direct3D12);
        FaultPlottable fault = new();
        Control.Plot.PlottableList.Add(fault);
        try { await Control.RefreshAsync(); } catch { }
        Control.Plot.PlottableList.Remove(fault);
        Require(ReferenceEquals(Control.RenderException, fault.Failure) && Control.RenderBackend == AvaPlotRenderBackend.Direct3D12,
            "Runtime failure was hidden or selected a fallback");
        Check("runtime-failure", new { Error = Control.RenderException!.Message });
        SetStatus("Expected D3D12 runtime failure. The chart remains failed until explicit Retry or Skia selection.");
        await SnapshotAsync("runtime-failure");
    }

    private async Task ValidateUnavailableAsync()
    {
        int before = Control.Plot.RenderManager.RenderCount;
        Control.RenderBackend = AvaPlotRenderBackend.Direct3D12;
        try { await Control.RefreshAsync(); } catch { }
        Require(Control.RenderException is not null && Control.RenderBackend == AvaPlotRenderBackend.Direct3D12 &&
            Control.Plot.RenderManager.RenderCount == before, "Unavailable GPU provider silently fell back");
        Program.Save("expected-failure.json", new { Pass = true, Backend = Control.RenderBackend.ToString(), Error = Control.RenderException!.ToString(), ErrorEvents });
        SetStatus("PASS: expected unavailable D3D12 error surfaced; no automatic fallback");
        await SnapshotAsync("expected-failure");
    }

    private void Check(string name, object details) => Checks.Add(new { Case = name, Pass = true, Details = details });
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    /// <summary>Injects a managed runtime rendering failure into the real GPU pipeline.</summary>
    private sealed class FaultPlottable : IPlottable
    {
        internal readonly Exception Failure = new InvalidOperationException("Injected full-Figure render failure");
        /// <inheritdoc />
        public bool IsVisible { get; set; } = true;
        /// <inheritdoc />
        public IAxes Axes { get; set; } = new Axes();
        /// <inheritdoc />
        public IEnumerable<LegendItem> LegendItems => [];
        /// <inheritdoc />
        public AxisLimits GetAxisLimits() => AxisLimits.Default;
        /// <inheritdoc />
        public void Render(RenderPack rp) => throw Failure;
    }
}
