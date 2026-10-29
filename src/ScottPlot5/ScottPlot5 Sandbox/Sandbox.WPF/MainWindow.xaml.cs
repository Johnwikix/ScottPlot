using System;
using System.Windows;
using ScottPlot;

namespace Sandbox.WPF;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        const int curveCount = 32;
        const int pointCount = 20_000;
        double[] xs = new double[pointCount];
        for (int i = 0; i < pointCount; i++)
            xs[i] = i * 0.01;

        for (int curve = 0; curve < curveCount; curve++)
        {
            double[] ys = new double[pointCount];
            for (int i = 0; i < pointCount; i++)
            {
                ys[i] = Math.Sin(xs[i] * (1 + curve % 5) + curve * 0.1) +
                    Math.Sin(xs[i] * 50 + curve) * 0.2;
            }

            var signal = WpfPlot1.Plot.Add.D3D12SignalXY(xs, ys);
            signal.LineWidth = 2;
        }

        D3D12Registration = WpfPlot1.Plot.UseD3D12();
        WpfPlot1.Plot.Title("D3D12 SignalXY - 32 curves / 20,000 points");
        WpfPlot1.Plot.Axes.AutoScale();
        WpfPlot1.Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        D3D12Registration.Dispose();
        base.OnClosed(e);
    }
}
