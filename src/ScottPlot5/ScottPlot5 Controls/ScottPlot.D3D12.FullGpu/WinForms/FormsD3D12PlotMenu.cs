using ScottPlot.D3D12.Rendering;
using ScottPlot.WinForms;

namespace ScottPlot.D3D12;

/// <summary>Provides standard WinForms menu commands with GPU raster exports and GPU viewer windows.</summary>
public sealed class FormsD3D12PlotMenu : FormsPlotMenu, IPlotMenu
{
    private readonly FormsPlotD3D12 Owner;

    /// <summary>Initializes a new instance of the <see cref="FormsD3D12PlotMenu"/> class.</summary>
    /// <param name="control">The owning GPU plot control.</param>
    public FormsD3D12PlotMenu(FormsPlotD3D12 control) : base(control)
    {
        Owner = control;
        Reset();
    }

    /// <summary>Creates standard commands that use the D3D12 renderer.</summary>
    /// <returns>The default menu entries.</returns>
    public new List<ContextMenuItem> StandardContextMenuItems() =>
    [
        new() { Label = "Save Image", OnInvoke = OpenSaveImageDialog },
        new() { Label = "Copy to Clipboard", OnInvoke = CopyImageToClipboard },
        new() { Label = "Autoscale", OnInvoke = Autoscale },
        new() { Label = "Open in New Window", OnInvoke = OpenInNewWindow },
    ];

    /// <inheritdoc/>
    public new void Reset() { Clear(); ContextMenuItems.AddRange(StandardContextMenuItems()); }

    /// <summary>Saves a selected plot using a GPU raster capture or the standard SVG vector export.</summary>
    /// <param name="plot">The selected subplot.</param>
    public new void OpenSaveImageDialog(Plot plot)
    {
        using SaveFileDialog dialog = new()
        {
            FileName = DefaultSaveImageFilename,
            Filter = "PNG Files (*.png)|*.png|JPEG Files (*.jpg)|*.jpg|BMP Files (*.bmp)|*.bmp|WebP Files (*.webp)|*.webp|SVG Files (*.svg)|*.svg",
        };
        if (dialog.ShowDialog(Owner) != DialogResult.OK)
            return;

        PixelSize size = plot.RenderManager.LastRender.FigureRect.Size;
        int width = Math.Max(1, (int)size.Width), height = Math.Max(1, (int)size.Height);
        try
        {
            ImageFormat format = ImageFormats.FromFilename(dialog.FileName);
            if (format == ImageFormat.Svg)
                plot.SaveSvg(dialog.FileName, width, height);
            else
            {
                using Image image = D3D12ImageExport.Capture(plot, width, height);
                image.Save(dialog.FileName, format);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(Owner, ex.Message, "Image save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally { Owner.Refresh(); }
    }

    /// <summary>Copies an explicitly requested GPU-rendered subplot to the clipboard.</summary>
    /// <param name="plot">The selected subplot.</param>
    public new void CopyImageToClipboard(Plot plot)
    {
        PixelSize size = plot.RenderManager.LastRender.FigureRect.Size;
        using Image image = D3D12ImageExport.Capture(plot, Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height));
        using MemoryStream stream = new(image.GetImageBytes());
        using System.Drawing.Bitmap bitmap = new(stream);
        Clipboard.SetImage(bitmap);
        Owner.Refresh();
    }

    /// <summary>Opens the selected plot in an interactive D3D12 viewer.</summary>
    /// <param name="plot">The selected subplot.</param>
    public new void OpenInNewWindow(Plot plot)
    {
        IPlotControl? previous = plot.PlotControl;
        FormsPlotD3D12 viewer = new() { Dock = DockStyle.Fill };
        viewer.Reset(plot);
        Form window = new() { Text = "Interactive Plot / D3D12", ClientSize = new System.Drawing.Size(900, 650) };
        window.Controls.Add(viewer);
        window.FormClosed += (_, _) =>
        {
            if (ReferenceEquals(plot.PlotControl, viewer))
                plot.PlotControl = previous;
            Owner.Refresh();
        };
        window.Show(Owner.FindForm());
    }

    /// <inheritdoc/>
    public new void ShowContextMenu(Pixel pixel)
    {
        Plot? plot = Owner.GetPlotAtPixel(pixel);
        if (plot is null || ContextMenuItems.Count == 0)
            return;
        ContextMenuStrip menu = GetContextMenu(plot);
        // The item click is dispatched after menu closure. Dispose on the next UI turn.
        menu.Closed += (_, _) =>
        {
            if (Owner.IsHandleCreated && !Owner.IsDisposed)
                Owner.BeginInvoke((Action)menu.Dispose);
            else
                menu.Dispose();
        };
        menu.Show(Owner, new System.Drawing.Point((int)pixel.X, (int)pixel.Y));
    }
}
