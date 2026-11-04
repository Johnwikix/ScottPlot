using NUnit.Framework;
using SkiaSharp;

namespace ScottPlot.D3D12.Tests;

/// <summary>Exercises the production WinForms HWND presenter and control lifecycle on real hardware.</summary>
[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class WinFormsControlTests
{
    /// <summary>Checks complete native presentation, explicit capture accounting and retained buffer sizes.</summary>
    [Test]
    public void Test_WinForms_FullFigurePresentsWithoutReadback()
    {
        using Form window = new();
        using ProbeControl control = Create(window);
        control.DrawFrame();
        Assert.Multiple(() =>
        {
            Assert.That(control.GRContext.Backend, Is.EqualTo(GRBackend.Direct3D));
            Assert.That(control.PresentedFrames, Is.GreaterThan(0));
            Assert.That(control.PixelReadbackCount, Is.Zero);
            Assert.That(control.LastGpuFrame.Width, Is.EqualTo(control.ClientSize.Width));
            Assert.That(control.LastGpuFrame.Height, Is.EqualTo(control.ClientSize.Height));
        });
        int resizes = control.SwapChainResizeCount;
        for (int i = 0; i < 5; i++) control.DrawFrame();
        Assert.That(control.SwapChainResizeCount, Is.EqualTo(resizes));
        using SKBitmap bitmap = control.CaptureBitmap();
        Assert.That(bitmap.Width, Is.EqualTo(control.ClientSize.Width));
        Assert.That(bitmap.GetPixel(1, 1), Is.EqualTo(new SKColor(255, 0, 0, 255)));
        Assert.That(control.PixelReadbackCount, Is.EqualTo(1));
        control.DrawFrame();
        Assert.That(control.PixelReadbackCount, Is.EqualTo(1));
    }

    /// <summary>Checks that resize, zero area and handle replacement produce usable independent swapchains.</summary>
    [Test]
    public void Test_WinForms_ResizeZeroSizeAndHandleRecreation()
    {
        using Form window = new();
        using ProbeControl control = Create(window);
        control.DrawFrame();
        int resizes = control.SwapChainResizeCount;
        control.ClientSize = new System.Drawing.Size(413, 277);
        control.DrawFrame();
        Assert.That(control.LastGpuFrame.Width, Is.EqualTo(413));
        Assert.That(control.SwapChainResizeCount, Is.EqualTo(resizes + 1));
        long frames = control.PresentedFrames;
        control.ClientSize = System.Drawing.Size.Empty;
        control.DrawFrame();
        Assert.That(control.PresentedFrames, Is.EqualTo(frames));
        control.ClientSize = new System.Drawing.Size(600, 400);
        control.RecreateWindow();
        Assert.That(control.PixelReadbackCount, Is.Zero);
        control.DrawFrame();
        Assert.That(control.PresentedFrames, Is.GreaterThan(0));
        Assert.That(control.LastGpuFrame.Width, Is.EqualTo(600));
        Assert.That(control.SwapChainResizeCount, Is.EqualTo(1));
    }

    /// <summary>Checks that queued worker refreshes repaint on the owner thread while direct GPU access fails.</summary>
    [Test]
    public void Test_WinForms_WorkerRefreshAndThreadAffinity()
    {
        using Form window = new();
        using ProbeControl control = Create(window);
        control.DrawFrame();
        long before = control.PresentedFrames;
        Task.Run(() => { for (int i = 0; i < 1000; i++) control.Refresh(); }).GetAwaiter().GetResult();
        Application.DoEvents();
        control.Update();
        Assert.That(control.PresentedFrames, Is.GreaterThan(before));
        Exception? error = Task.Run(() =>
        {
            try { _ = control.GRContext; return null; }
            catch (Exception ex) { return ex; }
        }).GetAwaiter().GetResult();
        Assert.That(error, Is.TypeOf<InvalidOperationException>());
        Assert.That(control.PixelReadbackCount, Is.Zero);
    }

    /// <summary>Checks inherited multiplot/reset APIs and that menu reset retains GPU command handlers.</summary>
    [Test]
    public void Test_WinForms_MultiplotResetAndMenu()
    {
        using Form window = new();
        using ProbeControl control = Create(window);
        using Plot second = control.Multiplot.AddPlot();
        second.Add.Bars(new double[] { 2, 3, 1 });
        control.DrawFrame();
        Assert.That(second.RenderManager.LastRender.DataRect.HasArea, Is.True);
        using Plot replacement = new();
        replacement.Add.Signal(Generate.Sin(100));
        control.Reset(replacement);
        control.DrawFrame();
        Assert.That(control.Plot, Is.SameAs(replacement));
        Assert.That(replacement.PlotControl, Is.SameAs(control));
        Assert.That(control.Multiplot.Subplots.Count, Is.EqualTo(1));
        control.Menu!.Clear();
        control.Menu.Reset();
        Assert.That(control.Menu.ContextMenuItems, Has.Count.EqualTo(4));
        foreach (ContextMenuItem item in control.Menu.ContextMenuItems.Where(x => x.Label != "Autoscale"))
            Assert.That(item.OnInvoke.Target, Is.TypeOf<FormsD3D12PlotMenu>());
    }

    /// <summary>Checks that closing one shared-device control does not invalidate another control.</summary>
    [Test]
    public void Test_WinForms_SharedDeviceSurvivesOtherControlDisposal()
    {
        using Form window = new();
        using ProbeControl first = Create(window);
        using ProbeControl second = new() { ClientSize = new System.Drawing.Size(200, 200) };
        window.Controls.Add(second);
        first.DrawFrame();
        second.DrawFrame();
        Assert.That(first.GRContext, Is.SameAs(second.GRContext));
        first.Dispose();
        first.Dispose();
        first.Refresh();
        second.DrawFrame();
        Assert.That(second.PresentedFrames, Is.GreaterThan(1));
        Assert.That(second.PixelReadbackCount, Is.Zero);
    }

    /// <summary>Checks that capture before rendering fails instead of fabricating a CPU frame.</summary>
    [Test]
    public void Test_WinForms_CaptureRequiresCompletedFrame()
    {
        using ProbeControl control = new();
        Assert.Throws<InvalidOperationException>(() => control.CaptureBitmap());
        control.Dispose();
        Assert.Throws<ObjectDisposedException>(() => control.CaptureBitmap());
    }

    /// <summary>Checks native-control event forwarding, pan capture and focus-state cleanup.</summary>
    [Test]
    public void Test_WinForms_InputEventsZoomPanAndReleaseCapture()
    {
        using Form window = new();
        using ProbeControl control = Create(window);
        control.DrawFrame();
        Pixel center = control.Plot.RenderManager.LastRender.DataRect.Center;
        AxisLimits before = control.Plot.Axes.GetLimits();
        control.WheelAt(center);
        AxisLimits zoomed = control.Plot.Axes.GetLimits();
        Assert.That(zoomed.Right - zoomed.Left, Is.LessThan(before.Right - before.Left));
        control.PanAt(center);
        Assert.That(control.Plot.Axes.GetLimits().Left, Is.Not.EqualTo(zoomed.Left));
        Assert.That(control.Capture, Is.False);
        control.UserInputProcessor.KeyState.Add(Interactivity.StandardKeys.Control);
        control.LoseInputFocus();
        Assert.That(control.UserInputProcessor.KeyState.IsPressed(Interactivity.StandardKeys.Control), Is.False);
    }

    private static ProbeControl Create(Form window)
    {
        ProbeControl control = new() { ClientSize = new System.Drawing.Size(600, 400) };
        control.Plot.FigureBackground.Color = Colors.Red;
        control.Plot.Add.Signal(Generate.Sin(1000));
        control.Plot.Add.Bars(new double[] { .2, .4, .6 });
        control.Plot.Title("WinForms D3D12 / full Figure");
        control.Plot.ShowLegend();
        window.ClientSize = new System.Drawing.Size(800, 600);
        window.Controls.Add(control);
        window.Show();
        return control;
    }

    private sealed class ProbeControl : FormsPlotD3D12
    {
        internal void RecreateWindow() => RecreateHandle();
        internal void WheelAt(Pixel pixel) => OnMouseWheel(new MouseEventArgs(MouseButtons.None, 0, (int)pixel.X, (int)pixel.Y, 120));
        internal void PanAt(Pixel pixel)
        {
            OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, (int)pixel.X, (int)pixel.Y, 0));
            OnMouseMove(new MouseEventArgs(MouseButtons.Left, 0, (int)pixel.X + 40, (int)pixel.Y, 0));
            OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, (int)pixel.X + 40, (int)pixel.Y, 0));
        }
        internal void LoseInputFocus() => OnLostFocus(EventArgs.Empty);
        internal void DrawFrame()
        {
            using System.Drawing.Graphics graphics = CreateGraphics();
            using PaintEventArgs args = new(graphics, ClientRectangle);
            OnPaint(args);
        }
    }
}
