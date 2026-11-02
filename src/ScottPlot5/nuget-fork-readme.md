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

## Install

```powershell
# Full-GPU WPF control (renders the whole figure on the GPU)
dotnet add package Johnwikix.ScottPlot.D3D12

# Core library only (software rendering, as upstream)
dotnet add package Johnwikix.ScottPlot
```

Packages in the `Johnwikix.*` family depend only on each other — no dependency on the official `ScottPlot.*` package IDs.

## Source code

https://github.com/Johnwikix/ScottPlot (branch `d3d12`)

## Quality assurance

Every release is validated by an end-to-end suite that renders hundreds of scenes across WPF, WinUI 3, and UWP real windows and compares them against the software-Skia baseline (appearance and performance), plus hardware D3D12 unit tests.

## License

MIT — inherited from upstream [ScottPlot](https://github.com/ScottPlot/ScottPlot).
