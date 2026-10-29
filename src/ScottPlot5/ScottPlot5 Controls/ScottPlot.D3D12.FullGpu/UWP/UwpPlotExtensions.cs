using Windows.UI.Input;
using Windows.UI.Xaml.Input;
using System;
using Windows.Foundation;
using Windows.System;

namespace ScottPlot.D3D12.UWP;

internal static class UwpPlotExtensions
{
    internal static Pixel Pixel(this PointerRoutedEventArgs e, UwpPlotD3D12 plotControl)
    {
        Point position = e.GetCurrentPoint(plotControl).Position;
        position.X *= plotControl.DisplayScale;
        position.Y *= plotControl.DisplayScale;
        return position.ToPixel();
    }

    internal static Pixel ToPixel(this Point p)
    {
        return new Pixel((float)p.X, (float)p.Y);
    }

    internal static void ProcessMouseDown(this Interactivity.UserInputProcessor processor, UwpPlotD3D12 plotControl, PointerRoutedEventArgs e)
    {
        Pixel pixel = e.Pixel(plotControl);
        UpdateKeyStateFromModifiers(processor.KeyState, e.KeyModifiers);

        PointerUpdateKind kind = e.GetCurrentPoint(plotControl).Properties.PointerUpdateKind;
        Interactivity.IUserAction action = kind switch
        {
            PointerUpdateKind.LeftButtonPressed => new Interactivity.UserActions.LeftMouseDown(pixel),
            PointerUpdateKind.MiddleButtonPressed => new Interactivity.UserActions.MiddleMouseDown(pixel),
            PointerUpdateKind.RightButtonPressed => new Interactivity.UserActions.RightMouseDown(pixel),
            _ => new Interactivity.UserActions.Unknown(kind.ToString(), "pressed"),
        };
        processor.Process(action);
    }

    internal static void ProcessMouseUp(this Interactivity.UserInputProcessor processor, UwpPlotD3D12 plotControl, PointerRoutedEventArgs e)
    {
        Pixel pixel = e.Pixel(plotControl);
        UpdateKeyStateFromModifiers(processor.KeyState, e.KeyModifiers);

        PointerUpdateKind kind = e.GetCurrentPoint(plotControl).Properties.PointerUpdateKind;
        Interactivity.IUserAction action = kind switch
        {
            PointerUpdateKind.LeftButtonReleased => new Interactivity.UserActions.LeftMouseUp(pixel),
            PointerUpdateKind.MiddleButtonReleased => new Interactivity.UserActions.MiddleMouseUp(pixel),
            PointerUpdateKind.RightButtonReleased => new Interactivity.UserActions.RightMouseUp(pixel),
            _ => new Interactivity.UserActions.Unknown(kind.ToString(), "released"),
        };
        processor.Process(action);
    }

    internal static void ProcessMouseMove(this Interactivity.UserInputProcessor processor, UwpPlotD3D12 plotControl, PointerRoutedEventArgs e)
    {
        Pixel pixel = e.Pixel(plotControl);
        UpdateKeyStateFromModifiers(processor.KeyState, e.KeyModifiers);

        Interactivity.IUserAction action = new Interactivity.UserActions.MouseMove(pixel);
        processor.Process(action);
    }

    internal static void ProcessMouseWheel(this Interactivity.UserInputProcessor processor, UwpPlotD3D12 plotControl, PointerRoutedEventArgs e)
    {
        Pixel pixel = e.Pixel(plotControl);
        UpdateKeyStateFromModifiers(processor.KeyState, e.KeyModifiers);

        Interactivity.IUserAction action = e.GetCurrentPoint(plotControl).Properties.MouseWheelDelta > 0
            ? new Interactivity.UserActions.MouseWheelUp(pixel)
            : new Interactivity.UserActions.MouseWheelDown(pixel);

        processor.Process(action);
    }

    internal static void ProcessKeyDown(this Interactivity.UserInputProcessor processor, UwpPlotD3D12 plotControl, KeyRoutedEventArgs e)
    {
        Interactivity.Key key = e.ToKey();
        Interactivity.IUserAction action = new Interactivity.UserActions.KeyDown(key);
        processor.Process(action);
    }

    internal static void ProcessKeyUp(this Interactivity.UserInputProcessor processor, UwpPlotD3D12 plotControl, KeyRoutedEventArgs e)
    {
        Interactivity.Key key = e.ToKey();
        Interactivity.IUserAction action = new Interactivity.UserActions.KeyUp(key);
        processor.Process(action);
    }

    internal static Interactivity.Key ToKey(this KeyRoutedEventArgs e)
    {
        return e.Key switch
        {
            VirtualKey.Control => Interactivity.StandardKeys.Control,
            VirtualKey.LeftControl => Interactivity.StandardKeys.Control,
            VirtualKey.RightControl => Interactivity.StandardKeys.Control,

            VirtualKey.Menu => Interactivity.StandardKeys.Alt,
            VirtualKey.LeftMenu => Interactivity.StandardKeys.Alt,
            VirtualKey.RightMenu => Interactivity.StandardKeys.Alt,

            VirtualKey.Shift => Interactivity.StandardKeys.Shift,
            VirtualKey.LeftShift => Interactivity.StandardKeys.Shift,
            VirtualKey.RightShift => Interactivity.StandardKeys.Shift,
            _ => new Interactivity.Key(e.Key.ToString()),
        };
    }

    /// <summary>
    /// Updates KeyState with modifier keys from VirtualKeyModifiers
    /// </summary>
    private static void UpdateKeyStateFromModifiers(Interactivity.KeyboardState keyState, VirtualKeyModifiers keyModifiers)
    {
        keyState.Reset();

        if (keyModifiers.HasFlag(VirtualKeyModifiers.Control))
            keyState.Add(Interactivity.StandardKeys.Control);

        if (keyModifiers.HasFlag(VirtualKeyModifiers.Shift))
            keyState.Add(Interactivity.StandardKeys.Shift);

        if (keyModifiers.HasFlag(VirtualKeyModifiers.Menu))
            keyState.Add(Interactivity.StandardKeys.Alt);
    }

    public static Windows.UI.Core.CoreCursorType GetCursor(this Cursor cursor)
    {
        return cursor switch
        {
            Cursor.Arrow => Windows.UI.Core.CoreCursorType.Arrow,
            Cursor.No => Windows.UI.Core.CoreCursorType.UniversalNo,
            Cursor.Wait => Windows.UI.Core.CoreCursorType.Wait,
            Cursor.Hand => Windows.UI.Core.CoreCursorType.Hand,
            Cursor.Cross => Windows.UI.Core.CoreCursorType.Cross,
            Cursor.SizeAll => Windows.UI.Core.CoreCursorType.SizeAll,
            Cursor.SizeNorthSouth => Windows.UI.Core.CoreCursorType.SizeNorthSouth,
            Cursor.SizeWestEast => Windows.UI.Core.CoreCursorType.SizeWestEast,
            _ => throw new NotImplementedException(cursor.ToString()),
        };
    }
}


