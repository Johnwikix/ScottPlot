using System.Threading;
using System.Windows;
using System.Windows.Threading;
using NUnit.Framework;
using SkiaSharp;

namespace ScottPlot.D3D12.Tests;

/// <summary>Verifies WPF automatically replaces lost GPU resources without recreating the control.</summary>
[TestFixture, NonParallelizable, Apartment(ApartmentState.STA)]
public sealed class WpfRecoveryTests
{
    /// <summary>Simulates repeated device loss and verifies new contexts and visible output after each automatic rebuild.</summary>
    [Test]
    public void LostGpuContextsAreRebuiltWithoutRecreatingTheControl()
    {
        using WpfPlotD3D12 control = new();
        control.Plot.FigureBackground.Color = Colors.Blue;
        Window window = new()
        {
            Width = 420, Height = 320, Content = control,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        try
        {
            window.Show();
            PumpDispatcher();
            for (int attempt = 0; attempt < 2; attempt++)
            {
                GRContext previous = control.GRContext;
                Assert.That(control.IsRenderSuspended, Is.False);
                previous.AbandonContext();
                control.Refresh();
                PumpDispatcher();

                Assert.Multiple(() =>
                {
                    Assert.That(control.GRContext, Is.Not.SameAs(previous));
                    Assert.That(control.GRContext.IsAbandoned, Is.False);
                    Assert.That(control.IsRenderSuspended, Is.False);
                    Assert.That(control.LastGpuFrame.Width, Is.GreaterThan(0));
                });
                using SKBitmap bitmap = control.CaptureBitmap();
                Assert.That(bitmap.GetPixel(1, 1).Blue, Is.EqualTo(255));
            }
        }
        finally
        {
            control.Dispose();
            window.Close();
            PumpDispatcher();
        }
    }

    /// <summary>Interrupts an active render and verifies the delayed retry rebuilds and presents without another explicit refresh.</summary>
    [Test]
    public void DeviceLossDuringRenderingRetriesWithoutAnotherRefresh()
    {
        using WpfPlotD3D12 control = new();
        control.Plot.FigureBackground.Color = Colors.Blue;
        Window window = new()
        {
            Width = 420, Height = 320, Content = control,
            WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
        };
        try
        {
            window.Show();
            PumpDispatcher();
            GRContext previous = control.GRContext;
            bool interrupt = true;
            control.Plot.RenderManager.RenderStarting += (_, _) =>
            {
                if (!interrupt) return;
                interrupt = false;
                previous.AbandonContext();
                throw new InvalidOperationException("Simulated device loss during rendering.");
            };

            control.Refresh();
            PumpDispatcher();
            Assert.That(control.IsRenderSuspended, Is.True);
            PumpUntil(() => !control.IsRenderSuspended && control.LastGpuFrame.Width > 0);

            Assert.Multiple(() =>
            {
                Assert.That(control.GRContext, Is.Not.SameAs(previous));
                Assert.That(control.GRContext.IsAbandoned, Is.False);
            });
            using SKBitmap bitmap = control.CaptureBitmap();
            Assert.That(bitmap.GetPixel(1, 1).Blue, Is.EqualTo(255));
        }
        finally
        {
            control.Dispose();
            window.Close();
            PumpDispatcher();
        }
    }

    private static void PumpUntil(Func<bool> completed)
    {
        long deadline = Environment.TickCount64 + 5000;
        while (!completed())
        {
            if (Environment.TickCount64 >= deadline) Assert.Fail("Timed out waiting for automatic GPU recovery.");
            Thread.Sleep(20);
            PumpDispatcher();
        }
    }

    private static void PumpDispatcher()
    {
        DispatcherFrame frame = new();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }
}
