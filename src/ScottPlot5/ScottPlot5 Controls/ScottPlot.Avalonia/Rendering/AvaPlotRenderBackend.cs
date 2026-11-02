namespace ScottPlot.Avalonia;

/// <summary>Identifies the rendering path requested by an <see cref="AvaPlot"/>.</summary>
public enum AvaPlotRenderBackend
{
    /// <summary>Draws with the Skia canvas provided by Avalonia on the current platform.</summary>
    PlatformSkia,

    /// <summary>Requires the optional Windows D3D12 provider and never falls back to another backend.</summary>
    Direct3D12,
}
