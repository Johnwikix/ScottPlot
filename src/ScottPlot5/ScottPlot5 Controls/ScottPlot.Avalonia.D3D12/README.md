# ScottPlot.Avalonia.D3D12

Optional Windows hardware D3D12 rendering for the same cross-platform `ScottPlot.Avalonia.AvaPlot` control.

Keep the shared view project on `net8.0` / `net10.0`, referencing `ScottPlot.Avalonia`. Reference this package only from the Windows launcher targeting `net8.0-windows` / `net10.0-windows`.

```csharp
using ScottPlot.Avalonia.D3D12;

AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseScottPlotD3D12();
```

Select the backend explicitly in the Windows view configuration:

```csharp
plotControl.RenderBackend = AvaPlotRenderBackend.Direct3D12;
plotControl.Plot.Add.Signal(data);
await plotControl.RefreshAsync();
```

Or bind the `RenderBackend` Avalonia property in shared XAML. Its default is `PlatformSkia`, so Linux/macOS retain Avalonia's existing canvas path without loading this Windows package. Requesting Direct3D12 on another OS reports PlatformNotSupportedException.

The existing Plot / Multiplot / Reset / Menu / UserInputProcessor / Refresh APIs use logical pixels on both backends. GPU DPI scaling does not overwrite Plot.ScaleFactor. RefreshAsync propagates errors. Scheduled Refresh reports a latched RenderException, raises RenderFailed, and displays an error surface. It never switches to Skia after a D3D12 failure. Call RetryRenderAsync to recreate the renderer, or explicitly select PlatformSkia.

Detach releases GPU resources asynchronously; attach initializes them again. Use `await plotControl.DisposeAsync()` for permanent removal. The renderer and native context are thread-affine; the control marshals refresh, retry and disposal to its UI thread.

The backend requires a compositor exposing D3D11 shared-image / keyed-mutex interop, such as ANGLE. It selects the compositor's adapter LUID and shares D3D12 devices per UI thread and adapter. Complete Figures render to native MSAA4 targets, resolve and copy to a D3D11 keyed-mutex texture, then enter Avalonia's composition tree. No per-frame CPU readback or software fallback is used. Avalonia can perform additional GPU copies internally; this is not a zero-copy claim.

For diagnostics, `RenderBackendInstance` can be inspected as `D3D12AvaPlotRenderer`. It exposes LastFrame, CompletedFrames, PixelReadbackCount, PresentationCopies, target capacity and adapter name. CaptureBitmap performs one explicit diagnostic/export readback. OpenGL-only plottables are rejected.

Version 5.1.61-gpu.1 is a locally validated prerelease; installing the matching base Avalonia and D3D12 Core packages is required. No remote publication is performed by the build. Windows hardware/window validation does not establish Linux/macOS runtime acceptance or exhaustive native Skia behavior.
