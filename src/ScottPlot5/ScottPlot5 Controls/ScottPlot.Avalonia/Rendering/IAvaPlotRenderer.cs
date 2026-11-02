using System;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace ScottPlot.Avalonia;

/// <summary>Provides an optional composition renderer without adding native platform dependencies to AvaPlot.</summary>
/// <remarks>Calls and continuations run on the control's UI thread. Plot and pointer coordinates remain logical pixels.</remarks>
public interface IAvaPlotRenderer : IAsyncDisposable
{
    /// <summary>Gets the borrowed GPU context, or <see langword="null"/> before initialization.</summary>
    GRContext? GRContext { get; }

    /// <summary>Renders and presents the complete control through its composition surface.</summary>
    /// <param name="control">The control whose model and logical bounds are rendered.</param>
    /// <param name="cancellationToken">A token canceled when the control detaches or changes backend.</param>
    /// <returns>A task completing after the frame's composition batch is rendered.</returns>
    Task RenderAsync(AvaPlot control, CancellationToken cancellationToken);
}
