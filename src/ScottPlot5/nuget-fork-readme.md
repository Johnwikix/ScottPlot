# ScottPlot for .NET with full-GPU Direct3D 12 rendering (Johnwikix fork)

A maintained fork of [ScottPlot 5](https://github.com/ScottPlot/ScottPlot) — the free, open-source plotting library for .NET — extended with a Direct3D 12 hardware rendering backend. The plotting API is unchanged from ScottPlot 5.1.60; figures that are CPU-rasterization-bound upstream render many times faster because every frame is produced entirely on the GPU (Skia GPU context, native 4x MSAA, explicit GPU resolve, no raster-surface fallback).

This fork is published under the `Johnwikix.*` package IDs, so it installs side by side with the official `ScottPlot.*` packages without conflicts.

## Packages

| Package | Provides |
|---|---|
| [Johnwikix.ScottPlot](https://www.nuget.org/packages/Johnwikix.ScottPlot) | Core plotting library (MIT, API-compatible with ScottPlot 5) |
| [Johnwikix.ScottPlot.WPF](https://www.nuget.org/packages/Johnwikix.ScottPlot.WPF) | Classic WPF user controls (software Skia rendering) |
| [Johnwikix.ScottPlot.D3D12.Core](https://www.nuget.org/packages/Johnwikix.ScottPlot.D3D12.Core) | Full-GPU D3D12 renderer core (framework-agnostic) |
| [Johnwikix.ScottPlot.D3D12](https://www.nuget.org/packages/Johnwikix.ScottPlot.D3D12) | Full-GPU WPF control (D3DImage presentation, native MSAA) |
| [Johnwikix.ScottPlot.D3D12.WinUI](https://www.nuget.org/packages/Johnwikix.ScottPlot.D3D12.WinUI) | Full-GPU WinUI 3 control (SwapChainPanel) |
| [Johnwikix.ScottPlot.D3D12.UWP](https://www.nuget.org/packages/Johnwikix.ScottPlot.D3D12.UWP) | Full-GPU UWP control (SwapChainPanel) |
| [Johnwikix.ScottPlot.Avalonia](https://www.nuget.org/packages/Johnwikix.ScottPlot.Avalonia) | Avalonia user control (cross-platform, software Skia rendering) |
| [Johnwikix.ScottPlot.Avalonia.D3D12](https://www.nuget.org/packages/Johnwikix.ScottPlot.Avalonia.D3D12) | Full-GPU D3D12 backend for the Avalonia control (Windows only) |

## Install

```powershell
# Full-GPU WPF control (renders the whole figure on the GPU)
dotnet add package Johnwikix.ScottPlot.D3D12

# Full-GPU Avalonia backend (Windows launcher; shared views stay on Johnwikix.ScottPlot.Avalonia)
dotnet add package Johnwikix.ScottPlot.Avalonia.D3D12

# Core library only (software rendering, as upstream)
dotnet add package Johnwikix.ScottPlot
```

## Usage

Each GPU control is a drop-in replacement for its platform's stock ScottPlot control — same `Plot` API, but every frame is rendered on the GPU.

### WPF

```xml
<Window ...
        xmlns:spd="clr-namespace:ScottPlot.D3D12;assembly=ScottPlot.D3D12">
    <spd:WpfPlotD3D12 />
</Window>
```

### WinUI 3 and UWP

```xml
<Page ...
      xmlns:spd="using:ScottPlot.D3D12.WinUI"> <!-- UWP: using:ScottPlot.D3D12.UWP -->
    <spd:WinUIPlotD3D12 /> <!-- UWP: <spd:UwpPlotD3D12 /> -->
</Page>
```

### Avalonia

Shared views reference only `Johnwikix.ScottPlot.Avalonia` and stay cross-platform:

```xml
<UserControl ...
             xmlns:sp="clr-namespace:ScottPlot.Avalonia;assembly=ScottPlot.Avalonia">
    <sp:AvaPlot x:Name="PlotControl" />
</UserControl>
```

The Windows launcher (on `net8.0-windows`/`net10.0-windows`) additionally references `Johnwikix.ScottPlot.Avalonia.D3D12`, registers the backend at startup, and opts in per control:

```csharp
using ScottPlot.Avalonia;
using ScottPlot.Avalonia.D3D12;

AppBuilder.Configure<App>().UsePlatformDetect().UseScottPlotD3D12();
PlotControl.RenderBackend = AvaPlotRenderBackend.Direct3D12; // default remains PlatformSkia
await PlotControl.RefreshAsync();
```

On Linux/macOS the control keeps Avalonia's Skia path; requesting `Direct3D12` on another OS reports `PlatformNotSupportedException` instead of silently falling back. From there the standard ScottPlot API applies to all backends:

```csharp
PlotControl.Plot.Add.Signal(Generate.Sin(51));
```

Packages in the `Johnwikix.*` family depend only on each other — no dependency on the official `ScottPlot.*` package IDs.

## Source code

https://github.com/Johnwikix/ScottPlot (branch `d3d12`)

## Quality assurance

Every release is validated by an end-to-end suite that renders hundreds of scenes across WPF, WinUI 3, and UWP real windows and compares them against the software-Skia baseline (appearance and performance), real-window Avalonia backend validation (DPI scaling, resize, Skia/D3D12 switching, failure recovery), plus hardware D3D12 unit tests.

## License

MIT — inherited from upstream [ScottPlot](https://github.com/ScottPlot/ScottPlot).
