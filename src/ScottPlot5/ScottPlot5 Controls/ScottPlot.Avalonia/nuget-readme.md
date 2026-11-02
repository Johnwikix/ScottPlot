**[ScottPlot](https://scottplot.net) is a free and open-source plotting library for .NET** that makes it easy to interactively display large datasets.

[![](https://raw.githubusercontent.com/ScottPlot/ScottPlot/master/dev/graphics/ScottPlot.gif)](https://scottplot.net)

## Avalonia Quickstart

* https://scottplot.net/quickstart/avalonia/

## Optional Windows D3D12 backend

`AvaPlot.RenderBackend` defaults to `PlatformSkia` on all platforms. A Windows launcher can reference `ScottPlot.Avalonia.D3D12`, register it using `UseScottPlotD3D12()`, and explicitly select `AvaPlotRenderBackend.Direct3D12` on the same control. Shared views and Linux/macOS launchers reference only this base package.

`RefreshAsync()` propagates backend errors. Scheduled `Refresh()` coalesces requests and exposes failures through `RenderException`, `RenderFailed`, and an error surface. Direct3D12 never automatically switches to Skia. Use `RetryRenderAsync()` for explicit recreation or change `RenderBackend` deliberately. Detach releases optional GPU resources; `DisposeAsync()` permanently releases the control and its Plot.
