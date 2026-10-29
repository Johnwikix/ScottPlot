# ScottPlot.D3D12

Hardware D3D12 full-Figure rendering with SkiaSharp3.119.0, native4xMSAA and an explicit GPU resolve. The WPF package provides ScottPlot.D3D12.WpfPlotD3D12 (WpfPlotBase/IPlotControl API), including normal Plot/Multiplot, styling, menus, mouse/keyboard input and D3DImage presentation. Core, WinUI and modern .NET10 UWP adapters are separate packages.

No WARP adapter, CPU presentation surface or automatic CPU rendering fallback is created. Unsupported hardware/context creation fails explicitly. Normal render/present loops do not read pixels back. Explicit CaptureBitmap/image export reads the GPU result once.

CPU data sampling, geometry preparation and glyph preparation still occur. Existing Hittable/Draggable decorators retain ScottPlot's auxiliary CPU raster hit maps. Native Skia software-path selection has not been exhaustively traced; a GPU target is not proof that every internal Skia algorithm is GPU-only.

Validation on Intel Arc140T/Windows x64:21 hardware tests passed;403 catalogue scenes covering67/67concrete core plottables rendered successfully;402images matched ordinary GPU render actions byte-for-byte (Benchmark contains dynamic timing text). A nativeMSAA WPF complete-Figure window was captured using lvt. Corrected full-Figure wall time is about5-6ms for32x20000SignalXY plusBars/Text/axes/title/grid/legend; includes explicitresolve and actualfence completion, excludesPresent/compositor. Earlier2.45ms figures had a fence-event defect and are withdrawn.

At the user's request, exhaustive three-platform screenshots and long stress tests were curtailed to prioritize DisplayApp integration. WinUI/UWP library builds are verified; current production adapters' full window/memory acceptance is not complete. Intel WPF memory acceptance is explicitly skipped by user instruction. Classic UWP.NETNative is not supported by the modern.NET10UWP adapter.

Use WPF:
xmlns:gpu="clr-namespace:ScottPlot.D3D12;assembly=ScottPlot.D3D12"
<gpu:WpfPlotD3D12 x:Name="PlotControl"/>

CallPlotControl.Refresh() after changes as with WpfPlot. Dispose GPU controls on permanent removal; Unloaded also releases GPU resources and reload recreates them. Rendering is thread-affine; Refresh marshals UI requests. GPU resource leases are shared on each UI thread.

This is a locally distributed prerelease. No remote NuGet publication is performed.

Local refresh 5.1.60-gpu.2 (2026-10-29): package version and matching dependencies are refreshed; renderer behavior is unchanged. The measured DisplayApp GC regression was in its application-owned LowAllocationSignalXYSource interface path allocating one Pixel[] per signal per frame. That source now retains its pixel buffer and boxed view. Rebuild DisplayApp with the source fix; upgrading this package alone does not patch application-owned data sources. The 16x550000-point allocation comparison and 255 passing DisplayApp tests are documented in DisplayApp/Benchmarks/GpuGcProbe/README.md. This refresh reuses the prior GPU validation; full platform/window/stress acceptance was not rerun.
