using System;

namespace ScottPlot.Avalonia;

/// <summary>Describes a latched backend failure reported by an <see cref="AvaPlot"/>.</summary>
public sealed class AvaPlotRenderFailedEventArgs : EventArgs
{
    /// <summary>Initializes a new instance of the <see cref="AvaPlotRenderFailedEventArgs"/> class.</summary>
    /// <param name="backend">The requested backend that failed.</param>
    /// <param name="exception">The original initialization or presentation exception.</param>
    public AvaPlotRenderFailedEventArgs(AvaPlotRenderBackend backend, Exception exception)
    {
        Backend = backend; Exception = exception;
    }
    /// <summary>Gets the requested backend that failed.</summary>
    public AvaPlotRenderBackend Backend { get; }

    /// <summary>Gets the original failure, including any native or device error details.</summary>
    public Exception Exception { get; }
}
