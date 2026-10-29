# Active implementation checkpoint

Latest human instruction: Unattended autonomous completion of ScottPlot.D3D12 nupkg implementation/build/testing/local publication to D:\nugetLocal; integrate into D:\code\wpf\DisplayApp; remove obsolete production curve overlay from commit1ddbccf3. No commits or remote NuGet. Preserve all existing dirty ScottPlot files/old extension/probes. The old prohibition on DisplayApp production edits is superseded. User explicitly skips Intel WPF memory acceptance (likely D3D9On12); do not keep testing that.

Use lvt background UI, do not steal focus or touch mouse/keyboard. User grants unattended scope, so continue authorized work without questions. No subagents unless explicit human/skill instruction; none used.

Repositories:
- ScottPlot D:\code\classLib\ScottPlot\ScottPlot HEAD fb3f5aae90db568a51a0d2c90f3c570659b517a2, original4 tracked changes +old D3D12 extension/tests, all preserved. Full handoff D3D12-NATIVE-WPF-HANDOFF.md already read.
- DisplayApp D:\code\wpf\DisplayApp was CLEAN on latest task start. Baseline saved artifacts/.../displayapp-before. Targets net8.0-windows7.0, ScottPlot.WPF5.1.59, direct Vortice3.8.3 packages. Obsolete production overlay Rendering/CurveLayerD3D12.cs +Rendering/D3D11On12Interop.cs. ChartControl.xaml has WpfPlot +CurveLayerHost +CrosshairCanvas; .cs uses _gpuLayer and LowAllocationSignalXYSource. Old benchmark tree retains historical implementations; do not blanket delete useful SIMD source/tests or old benchmarks without checking references.

Authoritative correction:
- Old probes including D3D12SkiaSimdProbe use ManualResetEvent withoutReset; GPU-complete timing INVALID. Old2.45ms/P953.4 cannot be retained as verified end-to-end GPU completion. Report now prominently corrects this.
- SkiaSharp3.119 SKSurface.Flush(bool,bool) delegates GRContext.Flush; not per-surface native access. Public SKSurface has no GetBackendTexture.
- With correct AutoResetEvent, wrapping a single-sample texture with internalMSAA4 +Context/Surface.Flush leaves external texture empty. GPU Snapshot beforeFlush makes external content available. This is GPU-only but do not use CPU readback in a presentation loop.
- Current explicit nativeMSAA route WORKS: actual D3D12 resource sample4 -> GRD3DTextureResourceInfo SampleCount4 -> GRBackendRenderTarget -> SKSurface.Create(context,backendRT,TopLeft,Bgra8888). Full Plot.Render, Context.Flush+Submit, explicit ResolveSubresource to external single-sample resource, proper transitions and GPU fence. Direct one-time D3D12 readback (not Skia Snapshot) proves full Figure content. No per-frame readback.
- New AutoResetEvent helper currently checks fence once after WaitOne; production should use a while(fence.CompletedValue<target) loop to guard stale/spurious event races too.
- Correct explicit-resolve benchmark final plan: static median5.16/P9514.23ms, dynamic5.75/P957.02ms, 204552B/Figure,720 measured frames. Includes native resolve +actual fence, excludes Present. User already said performance enough and proceed, so don't keep special optimization work; report truthful times. Data sampling and bulk paths already optimized as independent probe.
- Strict no internal Skia SoftwarePathRenderer fallback remains unproven. GRContextOptions exposes only AvoidStencilBuffers, RuntimeProgramCacheSize, GlyphCacheTextureMaximumBytes, AllowPathMaskCaching, DoManualMipmapping, BufferMapThreshold. Do not claim GPU surface proves every native path is GPU. Native trace/custom build may be needed; no native2D engine rewrite authorized.

Current new next-stage source tree:
dev/D3D12SkiaPresentationProbe/
 Shared/ FastSignalSource.cs, BufferInterpolation.cs, BulkPathLine.cs, FigureFixture.cs (copies fromSIMD probe), GpuFigureSurface.cs (explicitMSAA backendRT+resolve, growablecapacity), ProbeMemory.cs, ResizeWorkload.cs, ExternalTextureDiagnostic.cs.
 Memory/ csproj +MemoryProgram.cs +GpuHarness.cs (copied then AutoResetEvent fixed; originalSIMD files unchanged). Exe modes:
   handoff, wrapped-texture-handoff, resolved-performance, visual, capacity, exact.
   NOTE capacity/exact/visual modes still contain earlier assumptions and do NOT yet consistently use new explicit resolve. Rewrite before relying on them.
 Wpf/ new copied/modified WpfProgram.cs +project. Uses FigureFixture full densePlot, capacity sharedtarget, D3DImage matching-size shared11/D3D9 texture, Image.Width/Height=capacity/dpi, rootClipToBounds. Source now uses FigureSurface.SubmitForExternalUse and AutoResetEvent. NOT yet rebuilt/launched after last explicitMSAA correction.
 WinUi/ copied old WinUiProgram.cs/project; only WaitEvent changed to AutoResetEvent. NOT yet integrated new sharedcore and now won't compile because Program.CreatePlot() source no longer linked.
 Uwp/ same status asWinUi. Assets/manifest copied. Needs build/output executable manifest update (newprojectname) before registration.
 global.json SDK10.0.401.

Runtime:
WPF PIDs3944 and33804 were closed with lvt WindowPattern.Close, resource-dispose marker files exist. No known running probes now.
Old WPF capacity +internalMSAA screenshot was white (externaltexture blank). Intel WPF memory testing subsequently explicitly skipped by user.
Older phase actual WPF/WinUI/UWP sample0 fullFigure screenshots exist artifacts/d3d12-skia-probe/2026-10-28; they are valid only for their old configurations, not currentMSAA4. UWP prior registration removed.

Evidence:
artifacts/d3d12-skia-simd/2026-10-28:
 earlier5variant and finalzero-marker6variant results, semantics17280 comparisons/4812096points,40mutations,6paths,7marker guard scenarios, fullFigure6imagebyteequivalence. Timings not GPU-complete dueeventbug; correctness sampling still useful.
artifacts/d3d12-skia-presentation/2026-10-28:
 fence-corrected/wrapped-handoff.json and pngs (Context/Surface blank; Snapshot works)
 fence-corrected/resolved-performance.json (authoritative timing +all720frames)
 fence-corrected/resolved-external.png (actual external D3D12 texture completefigure)
 native-msaa-fence-corrected/handoff.json (explicitNativeResolve positive; othermodesnegative)
 memory-capacity.json/exact.json are old internalMSAA hypotheses, not currentcorrectresolve acceptance.
 visual-corrected/ shows 0-3pixels differing between larger-capacityclip and exact surface (GPU AA edge differences), first visual-diagnostic/had majorlayoutdifference because Plot.Render(surface) uses Skia.LocalClipBounds expanded1px on each edge; preserveAPIclip semantics.
 wpf*/ white screenshots historicalfailure andcleanup markers.
Root D3D12-SKIA-GPU-PROBE-RESULTS.md corrected prominently.

Tentative production design (not created yet):
Create new family src/ScottPlot5/ScottPlot5 Controls/ScottPlot.D3D12.FullGpu/{Core,WPF,WinUI,UWP}; preserve old ScottPlot.D3D12 folder unchanged. Package IDs ScottPlot.D3D12.Core, ScottPlot.D3D12 (WPF main requested), ScottPlot.D3D12.WinUI, ScottPlot.D3D12.UWP. Namespace WPF ScottPlot.D3D12.WpfPlotD3D12 for compatibility. Version e.g.5.1.60-gpu.1 to avoid old5.1.60 cachednupkg. Also build matching local ScottPlot andScottPlot.WPF dependencies with PackageVersion override, not core source version edits. SharedCore no WPF dependency, net8/net10; WPF net8/net10; WinUI platformTFMs; UWP .NET10realWindows.UI.Xaml.
Need device/context lifecycle, sharedperUIthread leases, hardware-only fail, native4MSAA targets+explicitresolve, fence loops, capacityonecurrenttarget, realViewportDPI. D3DImage and WinUI/UWP swapchains adapter-specifichosts.
Can optimize straight SignalXY via temporary RenderPlottables wrapper while retaining original public plottables/data/API. Private raw double[] in SignalXYSourceDoubleArray not public; consider cached reflection vs corechange. Genericarray sources expose arrays; all unsupported/custom cases still ordinaryGPU Skia, never CPU surface. No core modifications authorized unnecessarily.
WPF inherit WpfPlotBase to preservePlot/Multiplot/Menu/Input APIs. Its Plot setter internal, Reset methods public; input helpers internal ->copy routing or publicUserInputProcessor extensionmethods. Existing old WpfPlotD3D12 routesinputmanually and is curvecomposite+CPUfallback; do not reuse that rendering code.
ScottPlot.WPF originalWpfPlot.PaintSurface uses Multiplot.Render(canvas,PixelRect0..surfaceLocalClipWidth), does not explicitly setPlot.ScaleFactor toDPI; need compare old/new at150% to preserve visualsemantics. WinUI baseline DOES set Plot.ScaleFactor=XamlRoot.RasterizationScale. Multiplot.Render currently Canvas.Scale accumulates acrossplots atnon1dpi; isolate Save/Restore perplot in backend if reproducing its publiclayoutlogic, avoid coreoverwrite.
No existing ScottPlot.UWP project found, implement IPlotControl analogous WinUI with Windows.UI.Xaml.
Need tests beyond old369/369cookbook submission: currently60/67concreteIPlottable covered, missing Benchmark,DataStreamerXY,DraggablePlottableDecorator,HittablePlottableDecorator,IsoLines,SignalConst<T>,TINPlot. Fullstylevisual +threeplatformlvt realwindows still outstanding. Test package via consumers fromcleanlocalfeed after allvalidation, nooldnupkgreuse.
DisplayApp integration remove obsolete curveoverlay only; preservecrosshair/theme/data/interaction. LowAllocationSignalXYSource forList<double> stillusedandtests; decide whether package replacesit before deleting.

Build:
WPF/console dotnetdirectproject. WinUI must use VSMSBuild:
C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe
UWP modern .NET10 UseUwp=true UseUwpTools=false selfcontained CoreCLR, Main MTAThread. RealAppContainer manifest. New UWP registration/launch not done in this stage.
lvt C:\Users\90684\tools\lvt\lvt.exe. Must run lvt/Get-Process with require_escalated to see actual user-owned probes; sandboxcalls misleadingly show noPID/ownershipLost.
Tools outside allowed roots require_escalated. New permissionsinstruction justification should be short question. User fullyauthorizes scopedwork, don'taskchatconfirmation.
Skills read lvt, csharp-docs, winui3-gc-stw-analysis (andrefs earlier). No need reread. C# publicmembers XMLdocs.
No AGENTS.md found in relevantroots/parents inreadssofar.

Next action: build/render WPF corrected nativeMSAA screenshot (presentationonly,noIntelmemory), then implement productsharedcore/WPF andnativepanelhosts, validate, package, integrate DisplayApp, remove oldoverlay, runtests, localpublish only.

## 2026-10-29 local package delivery and DisplayApp integration

The production family now exists at src/ScottPlot5/ScottPlot5 Controls/ScottPlot.D3D12.FullGpu.
ScottPlot.D3D12 is the WPF package; Core, WinUI and modern .NET10 UWP are separate packages.
Six matching 5.1.60-gpu.1 nupkg files (including ScottPlot and ScottPlot.WPF dependencies) were generated from fresh independent output folders and copied to D:\nugetLocal without overwriting existing packages.
Release evidence: artifacts/d3d12-fullgpu/2026-10-29/release-113352/package-manifest.json.
Build-LocalPackages.ps1 provides a future build; it rejects existing package versions and runs hardware tests unless explicitly skipped. Its PowerShell syntax was checked; the delivered build used equivalent commands recorded in release logs, not a second run of this script.

DisplayApp now restores ScottPlot.D3D12 5.1.60-gpu.1 from its added local feed and uses WpfPlotD3D12. Removed obsolete production CurveLayerD3D12/D3D11On12Interop, Image overlay, GPU toggle and direct Vortice references. Kept LowAllocationSignalXYSource and historical benchmark hosts; legacy CPU benchmark selection now rejects clearly.
DisplayApp Release: 0 errors (existing warnings remain). Existing unit tests: 253/253 passed. Historical benchmark compiles; it was not launched because it moves the pointer.
Real production ChartControl package smoke: two 2000-point signals plus Bar/Text/axes/title/grid/legend, GRContext Direct3D, lvt UIA window screenshot verified, one explicit diagnostic capture, GPU control disposed successfully. No focus or cursor manipulation.
Evidence: D:\code\wpf\DisplayApp\artifacts\d3d12-package-smoke-final and Benchmarks/FullGpuSmoke/README.md.

Core hardware tests: 21/21 passed. Catalogue: 403/403 scenes, 67/67 concrete IPlottable types; 402 byte-identical to ordinary GPU render actions, Benchmark timing text varies.
52/52 preexisting protected files have unchanged hashes: artifacts/d3d12-fullgpu/2026-10-29/final-preservation.json. ScottPlot core source and original Sandbox changes were not modified.

Per the quota-saving request, production WinUI/UWP libraries were compiled/packed but full three-platform screenshots, interactions and long resize-memory stress were curtailed. Intel WPF memory acceptance was explicitly waived. Previous probes do not establish complete acceptance of these new production controls.
Strict tracing proving every native Skia path avoids software rasterization remains unverified; application-level CPU surface/fallback is absent and ordinary presentation performs no pixel readback. Existing Hittable/Draggable auxiliary CPU hitmaps remain. UWP means modern .NET10 Windows.UI.Xaml; classic .NET Native is unsupported.
Corrected dense Figure measurements remain ~5-6ms with actual fence completion; earlier ~2.45ms claims are withdrawn. The DisplayApp smoke single-frame time is not the dense benchmark.
No Git commit or remote NuGet publication was performed.
