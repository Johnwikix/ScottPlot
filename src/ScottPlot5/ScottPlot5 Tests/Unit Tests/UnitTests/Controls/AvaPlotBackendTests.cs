using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using ScottPlot.Avalonia;
using SkiaSharp;

namespace ScottPlotTests.Controls;

/// <summary>Creates an isolated, platform-neutral Avalonia test dispatcher with a real Skia canvas.</summary>
public static class AvaPlotTestApplication
{
    /// <summary>Creates the headless application used by backend lifecycle tests.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
        .UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

/// <summary>Tests host policy, logical coordinates, failure reporting and asynchronous resource ownership.</summary>
[TestFixture, NonParallelizable]
public class AvaPlotBackendTests
{
    private HeadlessUnitTestSession Session = null!;

    [OneTimeSetUp]
    public void SetUp() => Session = HeadlessUnitTestSession.StartNew(typeof(AvaPlotTestApplication));

    [OneTimeTearDown]
    public void TearDown() => Session.Dispose();

    [Test]
    public Task Test_DefaultSkia_DoesNotLoadOptionalProvider() => Run(async () =>
    {
        using var registration = AvaPlotRenderBackendRegistry.RegisterDirect3D12(() => throw new Exception("Provider must not be loaded"));
        await using AvaPlot control = new();
        Window window = Show(control);
        try
        {
            control.Plot.Add.Signal(new double[] { 1, 2, 3 });
            await Pump(control.RefreshAsync());
            Assert.Multiple(() =>
            {
                Assert.That(control.RenderBackend, Is.EqualTo(AvaPlotRenderBackend.PlatformSkia));
                Assert.That(control.RenderBackendInstance, Is.Null);
                Assert.That(control.RenderException, Is.Null);
                Assert.That(control.Plot.RenderManager.RenderCount, Is.GreaterThan(0));
            });
        }
        finally { window.Close(); }
    });

    [Test]
    public Task Test_MissingProvider_FailsExplicitlyWithoutSkiaRender() => Run(async () =>
    {
        await using AvaPlot control = new() { RenderBackend = AvaPlotRenderBackend.Direct3D12 };
        Window window = Show(control);
        try
        {
            Exception? caught = null;
            try { await control.RefreshAsync(); } catch (Exception error) { caught = error; }
            Assert.Multiple(() =>
            {
                Assert.That(caught, Is.Not.Null);
                Assert.That(control.RenderException, Is.Not.Null);
                Assert.That(control.RenderBackend, Is.EqualTo(AvaPlotRenderBackend.Direct3D12));
                Assert.That(control.Plot.RenderManager.RenderCount, Is.Zero);
            });
        }
        finally { window.Close(); }
    });

    [Test, Platform("Win")]
    public Task Test_FactoryFailure_IsLatchedAndExplicitRetryRecreatesRenderer() => Run(async () =>
    {
        Exception failure = new InvalidOperationException("Test initialization failure");
        bool fail = true;
        FakeRenderer renderer = new();
        int attempts = 0, events = 0;
        using var registration = AvaPlotRenderBackendRegistry.RegisterDirect3D12(() =>
        {
            attempts++;
            if (fail) throw failure;
            return renderer;
        });
        await using AvaPlot control = new() { RenderBackend = AvaPlotRenderBackend.Direct3D12 };
        control.RenderFailed += (_, e) => { Assert.That(e.Exception, Is.SameAs(failure)); events++; };
        Window window = Show(control);
        try
        {
            try { await control.RefreshAsync(); } catch { }
            try { await control.RefreshAsync(); } catch { }
            Assert.Multiple(() =>
            {
                Assert.That(control.RenderException, Is.SameAs(failure));
                Assert.That(attempts, Is.EqualTo(1));
                Assert.That(events, Is.EqualTo(1));
                Assert.That(control.Plot.RenderManager.RenderCount, Is.Zero);
            });
            fail = false;
            await control.RetryRenderAsync();
            Assert.Multiple(() =>
            {
                Assert.That(control.RenderException, Is.Null);
                Assert.That(control.RenderBackendInstance, Is.SameAs(renderer));
                Assert.That(renderer.Frames, Is.GreaterThan(0));
            });
        }
        finally { window.Close(); }
    });

    [Test, Platform("Win")]
    public Task Test_RuntimeFailure_PreservesBackendAndOriginalError() => Run(async () =>
    {
        FakeRenderer renderer = new();
        using var registration = AvaPlotRenderBackendRegistry.RegisterDirect3D12(() => renderer);
        await using AvaPlot control = new() { RenderBackend = AvaPlotRenderBackend.Direct3D12 };
        Window window = Show(control);
        try
        {
            await control.RefreshAsync();
            renderer.Failure = new InvalidOperationException("Test device removed");
            try { await control.RefreshAsync(); } catch { }
            Assert.Multiple(() =>
            {
                Assert.That(control.RenderException, Is.SameAs(renderer.Failure));
                Assert.That(control.RenderBackend, Is.EqualTo(AvaPlotRenderBackend.Direct3D12));
                Assert.That(control.Plot.RenderManager.RenderCount, Is.Zero);
            });
            control.RenderBackend = AvaPlotRenderBackend.PlatformSkia;
            await Pump(control.RefreshAsync());
            Assert.Multiple(() =>
            {
                Assert.That(renderer.Disposals, Is.EqualTo(1));
                Assert.That(control.RenderException, Is.Null);
                Assert.That(control.Plot.RenderManager.RenderCount, Is.GreaterThan(0));
            });
        }
        finally { window.Close(); }
    });

    [Test, Platform("Win")]
    public Task Test_DetachDuringFrame_CancelsThenReleasesAndReloads() => Run(async () =>
    {
        FakeRenderer first = new() { Delay = true }, second = new();
        int created = 0;
        using var registration = AvaPlotRenderBackendRegistry.RegisterDirect3D12(() => ++created == 1 ? first : second);
        await using AvaPlot control = new() { RenderBackend = AvaPlotRenderBackend.Direct3D12 };
        Window window = Show(control);
        try
        {
            Task frame = control.RefreshAsync();
            await first.Started.Task;
            window.Content = null;
            await frame;
            await PumpUntil(() => first.Disposals == 1);
            window.Content = control;
            window.UpdateLayout();
            await control.RefreshAsync();
            Assert.Multiple(() =>
            {
                Assert.That(first.Canceled, Is.True);
                Assert.That(first.Disposals, Is.EqualTo(1));
                Assert.That(control.RenderBackendInstance, Is.SameAs(second));
                Assert.That(control.RenderException, Is.Null);
            });
        }
        finally { window.Close(); }
    });

    [Test, Platform("Win")]
    public Task Test_RefreshBurst_CoalescesRequestsWhileFrameIsInFlight() => Run(async () =>
    {
        FakeRenderer renderer = new() { Hold = true };
        using var registration = AvaPlotRenderBackendRegistry.RegisterDirect3D12(() => renderer);
        await using AvaPlot control = new() { RenderBackend = AvaPlotRenderBackend.Direct3D12 };
        Window window = Show(control);
        try
        {
            control.Refresh();
            await renderer.Started.Task;
            for (int i = 0; i < 100; i++) control.Refresh();
            renderer.Hold = false;
            renderer.Release.TrySetResult();
            await PumpUntil(() => renderer.Frames >= 2);
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Assert.That(renderer.Frames, Is.EqualTo(2));
        }
        finally { window.Close(); }
    });

    [Test, Platform("Win")]
    public Task Test_ConcurrentDispose_AwaitsTheSamePendingResourceRelease() => Run(async () =>
    {
        FakeRenderer renderer = new() { HoldDispose = true };
        using var registration = AvaPlotRenderBackendRegistry.RegisterDirect3D12(() => renderer);
        await using AvaPlot control = new() { RenderBackend = AvaPlotRenderBackend.Direct3D12 };
        Window window = Show(control);
        try
        {
            await control.RefreshAsync();
            Task first = control.DisposeAsync().AsTask();
            Task second = control.DisposeAsync().AsTask();
            Assert.Multiple(() =>
            {
                Assert.That(first.IsCompleted, Is.False);
                Assert.That(second, Is.SameAs(first));
            });
            renderer.ReleaseDispose.TrySetResult();
            await Task.WhenAll(first, second);
            Assert.That(renderer.Disposals, Is.EqualTo(1));
        }
        finally { renderer.ReleaseDispose.TrySetResult(); window.Close(); }
    });

    [Test, Platform("Win")]
    public Task Test_RegistrationDisposedOutOfOrder_DoesNotRestoreDisposedFactory() => Run(async () =>
    {
        using var first = AvaPlotRenderBackendRegistry.RegisterDirect3D12(() => throw new Exception("Disposed provider must not be restored"));
        using var second = AvaPlotRenderBackendRegistry.RegisterDirect3D12(() => new FakeRenderer());
        first.Dispose(); second.Dispose();
        await using AvaPlot control = new() { RenderBackend = AvaPlotRenderBackend.Direct3D12 };
        Window window = Show(control);
        try
        {
            try { await control.RefreshAsync(); } catch { }
            Assert.That(control.RenderException?.Message, Does.Contain("not registered"));
        }
        finally { window.Close(); }
    });

    [TestCase(1), TestCase(1.25), TestCase(1.5), TestCase(2)]
    public Task Test_SkiaDpi_KeepsLogicalPointerAndUserScaleFactor(double scale) => Run(async () =>
    {
        await using AvaPlot control = new();
        control.Plot.ScaleFactor = 1.7;
        control.Plot.Layout.Fixed(new PixelPadding(25));
        control.Plot.Add.Signal(new double[] { 1, 2, 3 });
        control.Plot.Axes.SetLimits(-1, 4, -1, 4);
        Window window = Show(control);
        try
        {
            window.SetRenderScaling(scale);
            await Pump(control.RefreshAsync());
            Point point = new(200, 150);
            Pixel pixel = new((float)point.X, (float)point.Y);
            Coordinates before = control.Plot.GetCoordinates(pixel);
            double width = control.Plot.Axes.GetLimits().HorizontalSpan;
            window.MouseWheel(point, new Vector(0, 1));
            await Pump(control.RefreshAsync());
            Coordinates after = control.Plot.GetCoordinates(pixel);
            Assert.Multiple(() =>
            {
                Assert.That(control.Plot.ScaleFactor, Is.EqualTo(1.7).Within(1e-6));
                Assert.That(control.DisplayScale, Is.EqualTo(scale).Within(1e-6));
                Assert.That(control.Plot.Axes.GetLimits().HorizontalSpan, Is.LessThan(width));
                Assert.That(after.X, Is.EqualTo(before.X).Within(1e-6));
                Assert.That(after.Y, Is.EqualTo(before.Y).Within(1e-6));
            });
            Plot replacement = new();
            control.Reset(replacement);
            Assert.That(replacement.PlotControl, Is.SameAs(control));
        }
        finally { window.Close(); }
    });

    private Task Run(Func<Task> action) => Session.Dispatch(async () => { await action(); return true; }, CancellationToken.None);

    private static Window Show(AvaPlot control)
    {
        Window window = new() { Width = 400, Height = 300, Content = control };
        window.Show(); window.UpdateLayout();
        return window;
    }

    private static async Task Pump(Task task)
    {
        await PumpUntil(() => task.IsCompleted);
        await task;
    }

    private static async Task PumpUntil(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            timeout.Token.ThrowIfCancellationRequested();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            await Task.Delay(1, timeout.Token);
        }
    }

    /// <summary>Models failures and cancellation without creating a Windows GPU device.</summary>
    private sealed class FakeRenderer : IAvaPlotRenderer
    {
        internal int Frames, Disposals;
        internal bool Delay, Canceled, Hold, HoldDispose;
        internal Exception? Failure;
        internal readonly TaskCompletionSource Started = new();
        internal readonly TaskCompletionSource Release = new();
        internal readonly TaskCompletionSource ReleaseDispose = new();
        /// <inheritdoc />
        public GRContext? GRContext => null;
        /// <inheritdoc />
        public async Task RenderAsync(AvaPlot control, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            if (Failure is not null) throw Failure;
            if (Hold) await Release.Task.WaitAsync(cancellationToken);
            if (Delay)
            {
                try { await Task.Delay(Timeout.Infinite, cancellationToken); }
                catch (OperationCanceledException) { Canceled = true; throw; }
            }
            Frames++;
        }
        /// <inheritdoc />
        public async ValueTask DisposeAsync()
        {
            Disposals++;
            if (HoldDispose) await ReleaseDispose.Task;
        }
    }
}
