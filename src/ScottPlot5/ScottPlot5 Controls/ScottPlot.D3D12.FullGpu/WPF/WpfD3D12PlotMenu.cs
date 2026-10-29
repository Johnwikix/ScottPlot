using Microsoft.Win32;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ScottPlot.D3D12.Rendering;
using ScottPlot.WPF;

namespace ScottPlot.D3D12;

/// <summary>Retains the standard WPF plot menu while routing raster exports and new windows through D3D12.</summary>
public sealed class WpfD3D12PlotMenu : WpfPlotMenu, IPlotMenu
{
    private readonly WpfPlotD3D12 Owner;
    /// <summary>Initializes the GPU menu for its owning plot.</summary>
    /// <param name="control">The owning GPU plot control.</param>
    public WpfD3D12PlotMenu(WpfPlotD3D12 control) : base(control) { Owner = control; Reset(); }
    /// <summary>Creates the standard commands with GPU image and window handlers.</summary>
    /// <returns>The default menu entries.</returns>
    public new ContextMenuItem[] GetDefaultContextMenuItems() => [
        new() { Label = "Save Image", OnInvoke = OpenSaveImageDialog },
        new() { Label = "Copy to Clipboard", OnInvoke = CopyImageToClipboard },
        new() { Label = "Autoscale", OnInvoke = Autoscale },
        new() { Label = "Open in New Window", OnInvoke = OpenInNewWindow }
    ];
    /// <inheritdoc />
    public new void Reset() { Clear(); ContextMenuItems.AddRange(GetDefaultContextMenuItems()); }
    /// <summary>Exports one explicitly requested GPU image, preserving the existing SVG vector export API.</summary>
    /// <param name="plot">The selected plot.</param>
    public new void OpenSaveImageDialog(Plot plot)
    {
        SaveFileDialog dialog = new() { FileName = DefaultSaveImageFilename,
            Filter = "PNG Files (*.png)|*.png|JPEG Files (*.jpg)|*.jpg|BMP Files (*.bmp)|*.bmp|WebP Files (*.webp)|*.webp|SVG Files (*.svg)|*.svg" };
        if (dialog.ShowDialog(Window.GetWindow(Owner)) != true) return;
        PixelSize size = plot.RenderManager.LastRender.FigureRect.Size;
        int width = Math.Max(1, (int)size.Width), height = Math.Max(1, (int)size.Height);
        try
        {
            if (ImageFormats.FromFilename(dialog.FileName) == ImageFormat.Svg) plot.SaveSvg(dialog.FileName, width, height);
            else { using Image image = D3D12ImageExport.Capture(plot, width, height); image.Save(dialog.FileName); }
        }
        catch (Exception ex) { MessageBox.Show(Window.GetWindow(Owner), ex.Message, "Image save failed", MessageBoxButton.OK, MessageBoxImage.Error); }
        Owner.Refresh();
    }
    /// <summary>Copies one explicitly requested GPU image to the clipboard.</summary>
    /// <param name="plot">The selected plot.</param>
    public new static void CopyImageToClipboard(Plot plot)
    {
        PixelSize size = plot.RenderManager.LastRender.FigureRect.Size;
        using Image image = D3D12ImageExport.Capture(plot, Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height));
        using MemoryStream stream = new(image.GetImageBytes());
        BitmapImage bitmap = new(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        Clipboard.SetImage(bitmap);
    }
    /// <summary>Opens an interactive GPU viewer for the selected plot.</summary>
    /// <param name="plot">The selected plot.</param>
    public new void OpenInNewWindow(Plot plot)
    {
        IPlotControl? previous = plot.PlotControl;
        WpfPlotD3D12 viewer = new(); viewer.Reset(plot);
        Window window = new() { Title = "Interactive Plot / D3D12", Width = 900, Height = 650, Content = viewer, Owner = Window.GetWindow(Owner) };
        window.Closed += (_, _) => { viewer.Dispose(); if (ReferenceEquals(plot.PlotControl, viewer)) plot.PlotControl = previous; Owner.Refresh(); };
        window.Show();
    }
}
