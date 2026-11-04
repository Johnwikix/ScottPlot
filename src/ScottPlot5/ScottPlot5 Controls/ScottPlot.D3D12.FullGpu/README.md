# ScottPlot.D3D12

WinForms is available in the separate `ScottPlot.D3D12.WinForms` project/package (`net8.0-windows` and `net10.0-windows`).
Use `ScottPlot.D3D12.FormsPlotD3D12` in place of `ScottPlot.WinForms.FormsPlot`:

```csharp
var plotControl = new ScottPlot.D3D12.FormsPlotD3D12 { Dock = DockStyle.Fill };
Controls.Add(plotControl);
plotControl.Plot.Add.Signal(ScottPlot.Generate.Sin(1000));
plotControl.Plot.Axes.AutoScale();
plotControl.Refresh();
```

It inherits `FormsPlotBase` and retains Plot/Multiplot/Reset, standard mouse and keyboard input, custom menus and cursor APIs.
The complete Figure is copied on the GPU to a two-buffer HWND flip swapchain. Normal painting does not capture pixels.
The default menu uses GPU raster exports and a GPU viewer; SVG retains ScottPlot's vector export.
`CaptureBitmap()` reads back the last completed full viewport once and returns a caller-owned `SKBitmap`.
`LastGpuFrame`, `PresentedFrames`, `PixelReadbackCount`, and `SwapChainResizeCount` expose diagnostics.
Counters reset when the native handle/renderer is recreated. `LastGpuFrame` excludes presentation/compositor time.
Initialize high DPI mode before creating controls. Client layout and input coordinates are physical pixels, as in the standard WinForms control.
`Plot.ScaleFactor` remains application-controlled. GPU access and disposal require the creating UI thread;
`Refresh()` can queue from another thread after handle creation, but callers must separately synchronize data mutations.
Before the first handle exists, Refresh is ignored and initial WM_PAINT renders the current data.
Handle destruction releases GPU resources; a later paint recreates them. Ordinary WinForms painting is supported only for an opaque GPU viewport;
`DrawToBitmap`/GDI print capture and transparent parent-background composition are not GPU screenshot APIs.
Dispose the control on permanent removal. Device loss fails explicitly; recreate the control after resolving the failure.

The WinForms validation host is `dev/D3D12GpuValidation/WinForms`. Detailed Chinese acceptance steps, commands, evidence requirements,
and a prompt for the testing agent are in [WINFORMS-D3D12-TEST-HANDOFF.md](../../../../../WINFORMS-D3D12-TEST-HANDOFF.md).
WinForms runtime/GPU/window acceptance is pending; compilation does not establish that acceptance.

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
