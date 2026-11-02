using System;

namespace ScottPlot.Avalonia;

/// <summary>Registers the optional Windows renderer while keeping the base package platform neutral.</summary>
public static class AvaPlotRenderBackendRegistry
{
    private static readonly object Sync = new();
    private static Registration? Current;

    /// <summary>Registers the factory used for subsequently initialized Direct3D12 controls.</summary>
    /// <param name="factory">A factory that creates one renderer per control.</param>
    /// <returns>A registration lease restoring the preceding factory when disposed.</returns>
    /// <remarks>The Windows application retains the registration for its lifetime. Existing controls keep their renderer.</remarks>
    public static IDisposable RegisterDirect3D12(Func<IAvaPlotRenderer> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        lock (Sync)
        {
            return Current = new Registration(factory, Current);
        }
    }

    internal static IAvaPlotRenderer CreateDirect3D12()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("AvaPlot Direct3D12 requires Windows. Select PlatformSkia on Linux and macOS.");
        Func<IAvaPlotRenderer>? factory;
        lock (Sync) factory = Current?.Factory;
        if (factory is null)
            throw new InvalidOperationException("AvaPlot Direct3D12 is not registered. Reference ScottPlot.Avalonia.D3D12 in the Windows application and call UseScottPlotD3D12().");
        return factory() ?? throw new InvalidOperationException("The registered Direct3D12 renderer factory returned null.");
    }

    /// <summary>Restores a scoped provider registration without replacing a newer registration.</summary>
    private sealed class Registration : IDisposable
    {
        private bool Disposed;
        internal Func<IAvaPlotRenderer> Factory { get; }
        private Registration? Previous { get; }
        internal Registration(Func<IAvaPlotRenderer> installed, Registration? previous)
        {
            Factory = installed; Previous = previous;
        }
        /// <inheritdoc />
        public void Dispose()
        {
            lock (Sync)
            {
                if (Disposed) return;
                Disposed = true;
                while (Current is { Disposed: true }) Current = Current.Previous;
            }
        }
    }
}
