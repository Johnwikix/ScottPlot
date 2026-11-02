using Avalonia;

namespace ScottPlot.Avalonia.D3D12;

/// <summary>Registers the optional Windows backend without changing AvaPlot's default rendering policy.</summary>
public static class AppBuilderExtensions
{
    /// <summary>Registers the hardware D3D12 provider for controls explicitly selecting Direct3D12.</summary>
    /// <param name="builder">The Windows application's Avalonia builder.</param>
    /// <returns>The same builder for fluent application configuration.</returns>
    /// <exception cref="PlatformNotSupportedException">The application is not running on Windows.</exception>
    public static AppBuilder UseScottPlotD3D12(this AppBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The ScottPlot D3D12 provider requires Windows.");
        AvaPlotRenderBackendRegistry.RegisterDirect3D12(static () => new D3D12AvaPlotRenderer());
        return builder;
    }
}
