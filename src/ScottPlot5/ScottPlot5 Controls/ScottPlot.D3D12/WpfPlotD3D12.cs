using ScottPlot.D3D12.Rendering;
using ScottPlot.Rendering.RenderActions;
using ScottPlot.WPF;
using SkiaSharp;
using SkiaSharp.Views.WPF;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace ScottPlot.D3D12;

[TemplatePart(Name = BottomPartName, Type = typeof(SKElement))]
[TemplatePart(Name = CurvePartName, Type = typeof(System.Windows.Controls.Image))]
[TemplatePart(Name = TopPartName, Type = typeof(SKElement))]
public class WpfPlotD3D12 : WpfPlotBase, IDisposable
{
    private const string BottomPartName = "PART_BottomElement";
    private const string CurvePartName = "PART_CurveImage";
    private const string TopPartName = "PART_TopElement";

    private SKElement? BottomElement;
    private SKElement? TopElement;
    private D3D12NativePresenter? Presenter;
    private SKPicture? OverlayPicture;
    private Plot? ActivePlot;

    static WpfPlotD3D12()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(WpfPlotD3D12), new FrameworkPropertyMetadata(typeof(WpfPlotD3D12)));
    }

    public override GRContext GRContext => null!;
    protected override FrameworkElement PlotFrameworkElement => BottomElement!;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        DetachTemplate();
        BottomElement = Template.FindName(BottomPartName, this) as SKElement;
        TopElement = Template.FindName(TopPartName, this) as SKElement;
        System.Windows.Controls.Image? curveImage =
            Template.FindName(CurvePartName, this) as System.Windows.Controls.Image;
        if (BottomElement is null || TopElement is null || curveImage is null)
            return;

        Presenter = new D3D12NativePresenter(curveImage, Refresh);
        BottomElement.PaintSurface += (_, e) => OnBottomPaintSurface(e.Surface);
        TopElement.PaintSurface += (_, e) => OnTopPaintSurface(e.Surface);
        BottomElement.MouseDown += OnMouseDown;
        BottomElement.MouseUp += OnMouseUp;
        BottomElement.MouseMove += OnMouseMove;
        BottomElement.MouseWheel += OnMouseWheel;
        BottomElement.KeyDown += OnBottomKeyDown;
        BottomElement.KeyUp += OnBottomKeyUp;
        BottomElement.LostMouseCapture += OnLostCapture;
        BottomElement.LostKeyboardFocus += OnLostCapture;
    }

    public override void Refresh()
    {
        if (!CheckAccess())
        {
            Dispatcher.BeginInvoke(Refresh);
            return;
        }

        BottomElement?.InvalidateVisual();
        TopElement?.InvalidateVisual();
    }

    private void OnBottomPaintSurface(SKSurface surface)
    {
        float width = (float)surface.Canvas.LocalClipBounds.Width;
        float height = (float)surface.Canvas.LocalClipBounds.Height;
        PixelRect rect = new(0, width, height, 0);
        if (!ReferenceEquals(ActivePlot, Plot))
        {
            ClearComposite();
            ActivePlot = Plot;
        }

        if (!CanCompose())
        {
            RenderSoftware(surface.Canvas, rect);
            return;
        }

        List<IRenderAction> actions = Plot.RenderManager.RenderActions;
        List<IRenderAction> original = actions.ToList();
        int plottableIndex = original.FindIndex(x => x.GetType() == typeof(RenderPlottables));
        IRenderAction[] suffix = original.Skip(plottableIndex + 1).ToArray();
        D3D12CompositionAction composition = new(Presenter!, original[plottableIndex], suffix, SetOverlayPicture);

        lock (Plot.Sync)
        {
            try
            {
                actions.Clear();
                actions.AddRange(original.Take(plottableIndex));
                actions.Add(composition);
                Multiplot.Render(surface.Canvas, rect);
            }
            finally
            {
                actions.Clear();
                actions.AddRange(original);
            }
        }
    }

    private bool CanCompose()
    {
        return Presenter is not null &&
            Multiplot.Count() == 1 &&
            ReferenceEquals(Multiplot.GetPlot(0), Plot) &&
            Plot.RenderManager.RenderActions.Any(x => x.GetType() == typeof(RenderPlottables));
    }

    private void RenderSoftware(SKCanvas canvas, PixelRect rect)
    {
        ClearComposite();
        Multiplot.Render(canvas, rect);
    }

    private void OnTopPaintSurface(SKSurface surface)
    {
        surface.Canvas.Clear(SKColors.Transparent);
        if (OverlayPicture is not null)
            surface.Canvas.DrawPicture(OverlayPicture);
    }

    private void SetOverlayPicture(SKPicture? picture)
    {
        SKPicture? previous = OverlayPicture;
        OverlayPicture = picture;
        previous?.Dispose();
        TopElement?.InvalidateVisual();
    }

    private void ClearComposite()
    {
        Presenter?.Clear();
        SetOverlayPicture(null);
    }

    private void DetachTemplate()
    {
        if (BottomElement is not null)
        {
            BottomElement.MouseDown -= OnMouseDown;
            BottomElement.MouseUp -= OnMouseUp;
            BottomElement.MouseMove -= OnMouseMove;
            BottomElement.MouseWheel -= OnMouseWheel;
            BottomElement.KeyDown -= OnBottomKeyDown;
            BottomElement.KeyUp -= OnBottomKeyUp;
            BottomElement.LostMouseCapture -= OnLostCapture;
            BottomElement.LostKeyboardFocus -= OnLostCapture;
        }

        Presenter?.Dispose();
        Presenter = null;
        BottomElement = null;
        TopElement = null;
    }

    private void OnBottomKeyDown(object? sender, KeyEventArgs e) => base.OnKeyDown(e);
    private void OnBottomKeyUp(object? sender, KeyEventArgs e) => base.OnKeyUp(e);
    private void OnLostCapture(object? sender, EventArgs e) => UserInputProcessor.ProcessLostFocus();

    private void OnMouseDown(object? sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(this);
        SyncModifierKeys();
        Pixel pixel = GetPixel(e);
        Interactivity.IUserAction action = e.ChangedButton switch
        {
            MouseButton.Left => new Interactivity.UserActions.LeftMouseDown(pixel),
            MouseButton.Middle => new Interactivity.UserActions.MiddleMouseDown(pixel),
            MouseButton.Right => new Interactivity.UserActions.RightMouseDown(pixel),
            _ => new Interactivity.UserActions.Unknown(e.ChangedButton.ToString(), "pressed"),
        };
        UserInputProcessor.Process(action);
        BottomElement?.CaptureMouse();
    }

    private void OnMouseUp(object? sender, MouseButtonEventArgs e)
    {
        SyncModifierKeys();
        Pixel pixel = GetPixel(e);
        Interactivity.IUserAction action = e.ChangedButton switch
        {
            MouseButton.Left => new Interactivity.UserActions.LeftMouseUp(pixel),
            MouseButton.Middle => new Interactivity.UserActions.MiddleMouseUp(pixel),
            MouseButton.Right => new Interactivity.UserActions.RightMouseUp(pixel),
            _ => new Interactivity.UserActions.Unknown(e.ChangedButton.ToString(), "released"),
        };
        UserInputProcessor.Process(action);
        BottomElement?.ReleaseMouseCapture();
    }

    private void OnMouseMove(object? sender, MouseEventArgs e)
    {
        SyncModifierKeys();
        UserInputProcessor.Process(new Interactivity.UserActions.MouseMove(GetPixel(e)));
    }

    private void OnMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        SyncModifierKeys();
        Pixel pixel = GetPixel(e);
        Interactivity.IUserAction action = e.Delta > 0
            ? new Interactivity.UserActions.MouseWheelUp(pixel)
            : new Interactivity.UserActions.MouseWheelDown(pixel);
        UserInputProcessor.Process(action);
    }

    private Pixel GetPixel(MouseEventArgs e)
    {
        Point position = e.GetPosition(BottomElement);
        DpiScale dpi = VisualTreeHelper.GetDpi(BottomElement!);
        return new Pixel((float)(position.X * dpi.DpiScaleX), (float)(position.Y * dpi.DpiScaleY));
    }

    private void SyncModifierKeys()
    {
        ModifierKeys modifiers = Keyboard.Modifiers;
        UserInputProcessor.KeyState.Remove(Interactivity.StandardKeys.Alt);
        UserInputProcessor.KeyState.Remove(Interactivity.StandardKeys.Control);
        UserInputProcessor.KeyState.Remove(Interactivity.StandardKeys.Shift);
        if (modifiers.HasFlag(ModifierKeys.Alt)) UserInputProcessor.KeyState.Add(Interactivity.StandardKeys.Alt);
        if (modifiers.HasFlag(ModifierKeys.Control)) UserInputProcessor.KeyState.Add(Interactivity.StandardKeys.Control);
        if (modifiers.HasFlag(ModifierKeys.Shift)) UserInputProcessor.KeyState.Add(Interactivity.StandardKeys.Shift);
    }

    public void Dispose()
    {
        DetachTemplate();
        SetOverlayPicture(null);
        GC.SuppressFinalize(this);
    }
}
