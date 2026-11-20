using ScottPlot;
using ScottPlot.D3D12.Rendering;
using SkiaSharp;
using Vortice.Direct3D12;

/// <summary>Verifies device recovery through each production control in a real window.</summary>
internal static class DeviceRecoveryCheck
{
    internal static async Task RunAsync(IPlotControl control, Func<SKBitmap> capture,
        Func<long> presentedFrames, Func<D3D12DeviceLease> acquireDevice)
    {
        Plot plot = control.Plot;
        plot.FigureBackground.Color = Colors.Blue;
        control.Refresh();
        await WaitForAsync(() => presentedFrames() > 0);

        GRContext previous = control.GRContext ?? throw new InvalidOperationException("The initial frame has no GPU context.");
        long before = presentedFrames();
        bool interrupted = false;
        EventHandler<RenderPack> renderStarting = plot.RenderManager.RenderStarting;
        void InterruptFrame(object? sender, RenderPack args)
        {
            if (interrupted) return;
            interrupted = true;
            previous.AbandonContext();
            throw new InvalidOperationException("Injected loss during rendering.");
        }

        plot.RenderManager.RenderStarting += InterruptFrame;
        try
        {
            plot.FigureBackground.Color = Colors.Red;
            control.Refresh();
            await AssertRecoveredAsync(previous, before, new SKColor(255, 0, 0));
            if (!interrupted) throw new InvalidOperationException("The render interruption was not reached.");
        }
        finally { plot.RenderManager.RenderStarting = renderStarting; }

        previous = control.GRContext!;
        before = presentedFrames();
        plot.FigureBackground.Color = Colors.Blue;
        // Simulate native removal while idle. Release our references before waiting for recreation.
        using (D3D12DeviceLease lease = acquireDevice())
        using (ID3D12Device5 native = lease.Device.NativeDevice.QueryInterface<ID3D12Device5>())
        {
            if (!ReferenceEquals(previous, lease.Device.Context))
                throw new InvalidOperationException("The test did not acquire the control's graphics device.");
            native.RemoveDevice();
        }
        await AssertRecoveredAsync(previous, before, new SKColor(0, 0, 255));

        async Task AssertRecoveredAsync(GRContext oldContext, long oldFrameCount, SKColor color)
        {
            await WaitForAsync(() => presentedFrames() > oldFrameCount);
            if (control.GRContext is not { IsAbandoned: false } current || ReferenceEquals(oldContext, current))
                throw new InvalidOperationException("The control did not present with a new GPU context.");
            if (!ReferenceEquals(control.Plot, plot))
                throw new InvalidOperationException("Recovery replaced the plot.");
            using SKBitmap bitmap = capture();
            if (bitmap.GetPixel(1, 1) != color)
                throw new InvalidOperationException("The recovered frame did not contain the updated plot.");
        }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (!condition()) await Task.Delay(25, timeout.Token);
    }
}
