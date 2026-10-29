using Windows.UI.Xaml.Controls;
using ScottPlot.D3D12.Rendering;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace ScottPlot.D3D12.UWP;

/// <summary>Provides the standard plot menu with explicit GPU-rendered image exports.</summary>
public sealed class UwpPlotMenu : IPlotMenu, IDisposable
{
    private readonly UwpPlotD3D12 Control;
    private InMemoryRandomAccessStream? ClipboardStream;
    /// <summary>Gets or sets the suggested export filename.</summary>
    public string DefaultSaveImageFilename { get; set; } = "Plot.png";
    /// <inheritdoc />
    public List<ContextMenuItem> ContextMenuItems { get; set; } = [];
    /// <summary>Initializes the default menu for a native GPU plot control.</summary>
    /// <param name="control">The owning GPU plot.</param>
    public UwpPlotMenu(UwpPlotD3D12 control) { Control = control; Reset(); }
    /// <summary>Creates the standard save, copy and autoscale menu entries.</summary>
    /// <returns>The default menu entries.</returns>
    public ContextMenuItem[] GetDefaultContextMenuItems() => [
        new() { Label = "Save Image", OnInvoke = OpenSaveImageDialog },
        new() { Label = "Copy to Clipboard", OnInvoke = CopyImageToClipboard },
        new() { Label = "Autoscale", OnInvoke = Autoscale }
    ];
    /// <summary>Creates a native flyout for a selected subplot.</summary>
    /// <param name="plot">The selected plot.</param>
    /// <returns>A flyout containing the configured menu items.</returns>
    public MenuFlyout GetContextMenu(Plot plot)
    {
        MenuFlyout menu = new();
        foreach (ContextMenuItem item in ContextMenuItems)
        {
            if (item.IsSeparator) menu.Items.Add(new MenuFlyoutSeparator());
            else
            {
                MenuFlyoutItem entry = new() { Text = item.Label };
                entry.Click += (_, _) => item.OnInvoke(plot);
                menu.Items.Add(entry);
            }
        }
        return menu;
    }
    /// <summary>Prompts for an export filename; raster exports are rendered on the GPU and read once.</summary>
    /// <param name="plot">The plot to export.</param>
    public async void OpenSaveImageDialog(Plot plot)
    {
        FileSavePicker picker = new() { SuggestedFileName = DefaultSaveImageFilename };
        picker.FileTypeChoices.Add("PNG Files", new List<string> { ".png" });
        picker.FileTypeChoices.Add("JPEG Files", new List<string> { ".jpg", ".jpeg" });
        picker.FileTypeChoices.Add("BMP Files", new List<string> { ".bmp" });
        picker.FileTypeChoices.Add("WebP Files", new List<string> { ".webp" });
        picker.FileTypeChoices.Add("SVG Files", new List<string> { ".svg" });


        Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
        if (file is null) return;
        PixelSize size = plot.RenderManager.LastRender.FigureRect.Size;
        int width = Math.Max(1, (int)size.Width), height = Math.Max(1, (int)size.Height);
        if (ImageFormats.FromFilename(file.Name) == ImageFormat.Svg) { plot.SaveSvg(file.Path, width, height); return; }
        using Image image = D3D12ImageExport.Capture(plot, width, height);
        image.Save(file.Path);
    }
    /// <summary>Copies one explicit GPU-rendered image to the system clipboard.</summary>
    /// <param name="plot">The plot to copy.</param>
    public void CopyImageToClipboard(Plot plot)
    {
        PixelSize size = plot.RenderManager.LastRender.FigureRect.Size;
        using Image image = D3D12ImageExport.Capture(plot, Math.Max(1, (int)size.Width), Math.Max(1, (int)size.Height));
        byte[] png = image.GetImageBytes();
        ClipboardStream?.Dispose(); ClipboardStream = new();
        Stream output = ClipboardStream.AsStreamForWrite(); output.Write(png);
        ClipboardStream.Seek(0);
        DataPackage data = new(); data.SetBitmap(RandomAccessStreamReference.CreateFromStream(ClipboardStream)); Clipboard.SetContent(data);
    }
    /// <summary>Autoscales the selected plot and requests a full GPU redraw.</summary>
    /// <param name="plot">The selected plot.</param>
    public void Autoscale(Plot plot) { plot.Axes.AutoScale(); Control.Refresh(); }
    /// <inheritdoc />
    public void ShowContextMenu(Pixel pixel)
    {
        Plot? plot = Control.GetPlotAtPixel(pixel);
        if (plot is null || ContextMenuItems.Count == 0) return;
        GetContextMenu(plot).ShowAt(Control, new Windows.Foundation.Point(pixel.X / Control.DisplayScale, pixel.Y / Control.DisplayScale));
    }
    /// <inheritdoc />
    public void Reset() { Clear(); ContextMenuItems.AddRange(GetDefaultContextMenuItems()); }
    /// <inheritdoc />
    public void Clear() => ContextMenuItems.Clear();
    /// <inheritdoc />
    public void Add(string Label, Action<Plot> action) => Add(Label.AsSpan(), action);
    /// <summary>Adds a custom menu command while preserving the string API overload.</summary>
    /// <param name="label">The command label.</param>
    /// <param name="action">The command callback.</param>
    public void Add(ReadOnlySpan<char> label, Action<Plot> action) => ContextMenuItems.Add(new() { Label = label.ToString(), OnInvoke = action });
    /// <inheritdoc />
    public void AddSeparator() => ContextMenuItems.Add(new() { IsSeparator = true });
    /// <inheritdoc />
    public void Dispose() { ClipboardStream?.Dispose(); ClipboardStream = null; }
}

