using SkiaSharp;

namespace ScottPlot.D3D12.Rendering;

/// <summary>Creates explicit export images from full GPU-rendered Figures.</summary>
public static class D3D12ImageExport
{
    /// <summary>Renders a Plot through D3D12 and reads its pixels once for image export.</summary>
    /// <param name="plot">The caller-owned Plot.</param>
    /// <param name="width">The physical output width.</param>
    /// <param name="height">The physical output height.</param>
    /// <returns>A caller-owned image containing GPU-rendered pixels.</returns>
    public static Image Capture(Plot plot, int width, int height)
    {
        using D3D12Renderer renderer = new();
        renderer.Render(plot, width, height);
        using SKBitmap bitmap = renderer.CaptureBitmap();
        return new Image(bitmap);
    }
}
