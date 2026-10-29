# ScottPlot.D3D12：Skia GPU 第一阶段验证与设计记录

## 当前有效状态：fence/外部 MSAA resolve 修正（优先于下面的历史性能结论）

2026-10-28，下一阶段发现旧探针复用ManualResetEvent而未Reset，后续GPU fence等待可能提前返回。旧2.45ms及此前所有“GPU完成”时长不能当作已验证的GPU完成耗时，撤回P95<5ms通过结论；逐点/PNG语义对照仍有参考价值，原始证据保留。

SkiaSharp3.119的SKSurface.Flush仅转发GRContext.Flush。[官方源码](https://github.com/mono/SkiaSharp/blob/v3.119.0/binding/SkiaSharp/SKSurface.cs)可核对。在修正AutoResetEvent等待后，wrapped single-sample backend texture +内部MSAA4的context/surface flush后外部纹理仍未包含Figure；创建GPU Snapshot再flush可触发交接。该路径不能直接当作普通context flush即可外部呈现。

当前有效原型改为：创建真实D3D12四样本资源，公开GRBackendRenderTarget(sample4)包装GPU SKSurface，普通完整Plot.Render，再显式D3D12 ResolveSubresource到单样本外部纹理。自动复位fence后，一次性原始D3D12读回证明外部纹理包含完整彩色Figure；呈现循环没有读回。证据位于artifacts/d3d12-skia-presentation/2026-10-28/fence-corrected。

正确口径（三轮各120帧，每轮预热30，800×600同一完整Figure）当前测得：静态median5.16ms/P9514.23ms；全部Y原位更新median5.75ms/P957.02ms；204,552B/Figure。包括完整Render、native GPU resolve及真实fence等待，不含Present/合成器；未达5ms验收，不能继续引用旧2.45ms为GPU完成结果。用户已指示性能优化足够、继续后续实现，不再进行专项性能优化。

用户最新授权：无人值守完成ScottPlot.D3D12包实现/测试/本地发布到D:\nugetLocal，接入DisplayApp，移除DisplayApp的1ddbccf3中不再使用的曲线D3D叠加层。此前“不修改DisplayApp生产代码”的限制已被此次明确任务覆盖。保留当前工作区、不主动commit、不发布远程NuGet。用户还明确：Intel设备上的WPF内存无需验证；其内存验收跳过，仅验证呈现/交互，WinUI/UWP原生DXGI仍需内存验证。

全类型/全样式三个平台真实窗口、API兼容、严格无Skia内部软件path选路等仍未完成，不得把原型描述为完成的全图包。以下各节为历史记录，以本节修正为准。


## 2026-10-28 SIMD/路径验证更新：目标 Figure 已在离屏原型达到 P95 <5ms

新增独立 dev/D3D12SkiaSimdProbe，不改核心。固定800×600、32×20,000 SignalXY + Bar/Text/轴/标题/网格/图例、线宽2、AA=true、请求4×MSAA；每轮预热30帧+测120帧，三轮，每种场景360帧。计时包括完整 Plot.Render、Flush/Submit、D3D12 fence完成；无逐帧像素读回，不含Present/合成器。

最终同轮对照：原 source/path 完整重绘中位数14.76ms/P9524.67ms；可复用点缓冲+每帧共享X列计划+Vector256 SIMD+批量AddPoly/Rewind+严格限定的默认零面积marker无操作消除，降至 **2.447ms/P953.403ms**。全部Y原位更新为 **2.455ms/P953.408ms**。分配从6,760,192降至204,616B/Figure，仍非零分配。原SignalXY仅更换source约8.4ms；2.45ms还依赖探针IPlottable包装器，尚未产品化或修改核心。

最后约3ms开销来自默认size0的FilledCircle marker仍逐点调用填充；只有无custom renderer、无hatch、无可渲染outline的零大小圆形才跳过。可见marker、outline/hairline与custom renderer均保留，并通过聚焦的像素对照。六方案完整Figure PNG逐字节一致。17,280组/4,812,096个输出点语义对照、40次原位修改、6组路径结构对照通过，含NaN/无穷/负零、尖峰、重复X、轴反转、旋转和三种ConnectStyle；不等于全类型/样式验收。

本轮满足此设备/fixture中位数与P95的5ms目标，**不是逐帧硬上限**：动态max5.774ms，3/360帧超过5ms。Y数据更新另耗中位数0.627ms；将更新计入后median3.121ms/P954.314ms。尚未测GPU timestamp。CPU路线已达统计目标，未实施D3D Compute。

内存仍未通过：优化后1200帧定尺寸private约256-259MiB/local GPU84MiB；12×24尺寸循环private峰值725MiB、后段约564MiB，local约252MiB；显式Skia purge未明显回落。64MiB Skia cache限制不是进程/GPU总内存上限，不能仅凭该实验认定泄漏或已修复。未包括三平台present/compositor开销；Skia内部软件path选择trace仍缺失。

用户已指示停止进一步性能优化、进入下一步：将配置接入隔离的三平台呈现/生命周期探针，继续检查resize内存、DPI和后台lvt真实窗口。本轮不打包。

详细证据：[验证说明](artifacts/d3d12-skia-simd/2026-10-28/README.md)、[最终summary](artifacts/d3d12-skia-simd/2026-10-28/zero-marker/summary.json)、[逐帧数据](artifacts/d3d12-skia-simd/2026-10-28/zero-marker/frames.json)、[内存](artifacts/d3d12-skia-simd/2026-10-28/zero-marker/memory.json)。


## 2026-10-28 性能追查更新：单样本 AA 是主要慢路径

用户追问大数据性能后，新增独立后台 ProfileProbe，链接同一个 CreateDensePlot fixture，不修改核心/原探针，不开启窗口。全部用普通完整 Plot.Render，800×600、32×20,000、Bar/Text/轴/标题/网格/图例；每个方案预热6帧、测12帧。

同轮、未改 SignalXY.Render 的完整 Figure 中位数：

| 配置 | 完整 Figure | Plot.Render | Flush/Submit | 每帧托管分配 |
|---|---:|---:|---:|---:|
| 单样本 + 线条AA开启 | 206.02 ms | 200.96 ms | 3.41 ms | 6.76 MB |
| 单样本 + 仅线条AA关闭（定位对照） | 13.34 ms | 12.34 ms | 0.90 ms | 6.76 MB |
| 请求4×MSAA + 线条AA保持开启 | **13.48 ms** | 12.62 ms | 0.87 ms | 6.76 MB |

BGRA 最大支持 sample count 查询为8。无逐帧像素读回；每个方案的诊断 PNG 都在计时结束后编码。关闭 AA 仅作为诊断，不是满足原视觉语义的优化方案。MSAA方案未接入三平台宿主，尚未进行全样式视觉/内存回归，不能当成最终后端结果。

另以与固定 straight fixture 等价的分段 SignalXY/Drawing.DrawLines 逻辑定位调用成本，完整 Figure 仍保留，未改 core。各阶段独立中位数：

| 阶段（32条曲线合计） | 单样本AA | 4×MSAA且AA开启 |
|---|---:|---:|
| Data.GetPixelsToDraw | 7.68 ms | 7.60 ms |
| SKPath构造 | 2.45 ms | 2.50 ms |
| 原生 SKCanvas.DrawPath 调用 | **192.86 ms** | **0.108 ms** |
| 完整 Figure（分段计时有额外开销） | 209.54 ms | 15.58 ms |

每帧有效线点共85,248，输入点共640,000。原 SignalXY 每帧仍在CPU做可视范围查询、按像素列求min/max、点数组分配、SKPath重建；GPU target并未消除这些成本。分段包装器只用于固定straight诊断；正式性能对照以未改 SignalXY 的前表为准。不同阶段中位数不保证可以直接相加。

**修正前一轮解释**：159.37 ms 是当时单样本AA配置的真实结果，不是Skia D3D12在合理AA配置下的性能上限。此次复测单样本为206ms，存在轮次/环境差异；应使用同轮206ms与13.48ms的配置对照定位原因，不能把跨轮差值全部归因于优化。最初没有先做AA配置对照就概括大数据性能，是验证顺序上的遗漏。

Skia m119源码提供机制依据：[TessellationPathRenderer](https://github.com/google/skia/blob/chrome/m119/src/gpu/ganesh/ops/TessellationPathRenderer.cpp) 的 onCanDrawPath 明确拒绝 Coverage AA，而MSAA可进入不同路径。结合受控对照，可定位到单样本AA的原生DrawPath慢路径；未采集原生选路trace/调用栈，所以**尚不能断言实际采用的具体renderer，也不能把全部DrawPath墙钟成本解释为某个CPU算法或GPU执行时间**。

还有一项验收证据必须收紧：[SoftwarePathRenderer](https://github.com/google/skia/blob/chrome/m119/src/gpu/ganesh/ops/SoftwarePathRenderer.cpp) 明确存在复杂path的软件fallback。GPU-backed surface只能证明目标/context，不能单独排除Skia内部软件path。旧证据中的CpuFallbacks=0表示探针没有显式Raster surface回退分支，不是原生fallback事件计数。**全部类型/样式无内部软件回退仍未证明，不能判通过。** 本次没有证据证明该慢曲线实际进入SoftwarePathRenderer。

下一步性能工作应针对剩余CPU数据准备/数组/路径成本、Skia实际原生选路及MSAA内存代价；不因此自动修改核心、降低绘图语义或开始原生2D引擎重写。现有1.75–5.21ms专用叠加层和完整Figure口径不同，不能直接要求两者数字相等。

完整证据：[profile.json](artifacts/d3d12-skia-profile/2026-10-28/profile.json)、[summary.json](artifacts/d3d12-skia-profile/2026-10-28/summary.json)、[4×MSAA诊断图](artifacts/d3d12-skia-profile/2026-10-28/original-aa-msaa4.png)。本次新增 dev/D3D12SkiaProfileProbe，原26个探针源文件哈希未变。

日期：2026-10-28（Asia/Tokyo）。基线 HEAD：`fb3f5aae90db568a51a0d2c90f3c570659b517a2`。

## 结论

**SkiaSharp 3.119.0 的 D3D12 路线可行。已创建硬件 Direct3D context、将外部 D3D12 texture 包装为 GPU-backed SKSurface，并调用普通完整 Plot.Render。WPF、WinUI 3、UWP 均取得真实窗口的 lvt 截图。**

**完整扩展包尚未完成，尚未通过最终验收。resize 内存和平台生命周期仍是阻塞项；离屏性能原型进展见最新更新，全类型/样式视觉等价、原控件 API/交互、设备丢失尚未闭环。本轮不打包。**

| 项目 | 本轮状态 |
|---|---|
| 3.119.0 公开 .NET D3D12 API 和 x64 原生入口 | 已验证 |
| 外部 texture GPU surface + 普通完整 Figure | 已验证 |
| 硬件适配器、Direct3D backend、非 Raster surface | 已验证 |
| 测量/呈现循环没有像素读回，探针没有 CPU surface 回退 | 已验证探针调用路径，未全量审计 Skia 内部算法 |
| WPF D3DImage、WinUI 3 和 UWP 原生 SwapChainPanel 真实窗口 | 最小完整 Figure 已验证 |
| 369 个现有 Cookbook 单 Plot 配方 | 369/369 GPU 提交冒烟通过，覆盖核心 67 个具体 IPlottable 中的 60 个 |
| 全类型、全样式视觉等价、各类型在三个平台分别截图 | 未验证完成 |
| 32×20,000 完整 Figure 性能 | 最新离屏原型2.447ms/P953.403ms；平台端和正式API待验证 |
| resize 内存、长期稳态、平台生命周期 | 未通过验收；已有观察和隔离实验 |
| 正式共享核心、原 API 完整控件适配 | 仅设计，未产品化 |
| 新 NuGet、commit、远程发布 | 均未进行 |

历史 13/13 扩展测试和 486/486 上游测试本轮没有重跑，不能称为当前全图后端测试结果。

## 工作区与隔离

先完整阅读了 D3D12-NATIVE-WPF-HANDOFF.md，再检查工作区。开始时有 4 个 tracked 修改文件和未跟踪的旧 D3D12 控件/测试、交接文档。证据目录中的 status-before.txt、tracked-before.patch、hashes-before.json 保存初始状态。

结束时再次核对已有 23 个修改/未跟踪源文件的哈希，未改变。ScottPlot 核心、旧扩展/测试、旧交接文档和 DisplayApp 生产代码均未修改；没有 reset、commit 或远程发布。输出进入独立 artifacts 目录，没有复用旧 nupkg。

旧 Sandbox 仍有交接文档指出的命名空间/标签和 D3D12Registration 中断状态。选择**隔离验证**：新探针按项目直接构建，未加入旧 solution，不加载旧 WpfPlotD3D12/曲线扩展。WPF 只链接已有且经 SDK 头文件核对的 D3D11On12Interop.cs。本轮没有证明旧 Sandbox 或整个 solution 可构建。

## 固定版本 API、原生库与 GPU 证据

实际环境：Windows x64；.NET SDK 10.0.401 / runtime 10.0.12；Intel(R) Arc(TM) 140T GPU (15GB)，VendorId=32902，DeviceId=32081，DXGI AdapterFlags=None。拒绝 Software adapter，不使用 WARP。

本机 SkiaSharp 3.119.0.0 程序集反射确认：

~~~csharp
GRContext.CreateDirect3D(GRD3DBackendContext)
GRContext.CreateDirect3D(GRD3DBackendContext, GRContextOptions)
new GRBackendTexture(width, height, GRD3DTextureResourceInfo)
new GRBackendRenderTarget(width, height, GRD3DTextureResourceInfo)
SKSurface.Create(context, backendTexture, GRSurfaceOrigin.TopLeft, 0, SKColorType.Bgra8888)
~~~

context 接收真实 IDXGIAdapter / ID3D12Device / ID3D12CommandQueue 指针；texture info 接收真实 ID3D12Resource、状态、DXGI format、sample/mip count。实际运行了 texture 路径；render-target 构造 API/入口只核对存在，未单独运行该变体。

x64 DLL 实际加载并确认以下导出：

- gr_direct_context_make_direct3d
- gr_direct_context_make_direct3d_with_options
- gr_backendtexture_new_direct3d
- gr_backendrendertarget_new_direct3d
- sk_surface_new_backend_texture
- sk_surface_new_backend_render_target

x86、ARM64 DLL 的前四个 D3D 入口通过 dumpbin 静态核对，**未运行这些架构**。x64 DLL 直接依赖 d3d12.dll、D3DCOMPILER_47.dll。

| 文件 | SHA-256 |
|---|---|
| SkiaSharp.dll / lib/net8.0 / 3.119.0 | 00703ECD3654F52BC7AFA5D1721B56D07ED27F4F8CDDFC19F897A61966682582 |
| libSkiaSharp.dll / win-x64 / 3.119.0 | 96A6D73DC9B641A2F391AD09F15C264A1D910004773D262AEE7941A28E1A0029 |

GPU 探针的实际断言：

1. context.Backend == Direct3D；外部 texture wrapper.IsValid 且 Backend == Direct3D。
2. surface.Context 存在且 Backend == Direct3D；surface.PeekPixels() == null。
3. surface.Snapshot().IsTextureBacked == true。
4. 每个测量帧执行普通 Plot.Render、Skia flush/submit、同一 D3D12 queue 的 fence；检查 context 未 abandon、设备未 removed。
5. GPU 条件失败直接抛异常并写阶段，没有创建 Raster SKSurface 的回退分支。

这证明画面目标与提交后端为硬件 D3D12。没有 PIX/ETW GPU 轨迹或 GPU timestamps；不声称字体准备、路径处理、图像解码、布局等全部在 GPU。

官方固定版本和平台参考：

- [v3.119.0 GRContext](https://github.com/mono/SkiaSharp/blob/v3.119.0/binding/SkiaSharp/GRContext.cs)
- [v3.119.0 GRBackendTexture](https://github.com/mono/SkiaSharp/blob/v3.119.0/binding/SkiaSharp/GRBackendTexture.cs)
- [v3.119.0 GRBackendRenderTarget](https://github.com/mono/SkiaSharp/blob/v3.119.0/binding/SkiaSharp/GRBackendRenderTarget.cs)
- [DirectX / SwapChainPanel interop](https://learn.microsoft.com/en-us/windows/uwp/gaming/directx-and-xaml-interop)
- [SetSwapChain 的 UI 线程与引用释放要求](https://learn.microsoft.com/en-us/windows/win32/api/windows.ui.xaml.media.dxinterop/nf-windows-ui-xaml-media-dxinterop-iswapchainpanelnative-setswapchain)
- [交换链矩阵变换](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_3/nf-dxgi1_3-idxgiswapchain2-setmatrixtransform)
- [现代 .NET UWP](https://learn.microsoft.com/en-us/windows/uwp/dotnet-native/modernize-uwp-apps-with-dotnet)

## 真实窗口、DPI 和读回

普通测试图包含：2×1,000 点 Scatter（正弦、虚线余弦）、3 个 Bar、Text、标题、轴标签、刻度、网格、图例。没有调用 UseD3D12、使用 D3D12 专用 Plottable 或替换 RenderActions。

| 平台 | 实际链路 | 真实截图 |
|---|---|---|
| WPF | Skia D3D12 texture → D3D11On12 GPU copy → Shared D3D11 texture → D3D9Ex → D3DImage | [稳定帧](artifacts/d3d12-skia-probe/2026-10-28/wpf-stable-lvt.png)、[resize 后](artifacts/d3d12-skia-probe/2026-10-28/wpf-after-resize-lvt.png) |
| WinUI 3 | Skia D3D12 texture → GPU copy → D3D12 queue 创建的 DXGI composition swapchain → 原生面板 | [DPI 修正后](artifacts/d3d12-skia-probe/2026-10-28/winui-dpi-corrected-lvt.png)、[缩放后](artifacts/d3d12-skia-probe/2026-10-28/winui-zoom-lvt.png)、[resize 后](artifacts/d3d12-skia-probe/2026-10-28/winui-after-resize-lvt.png) |
| UWP | 同样的 D3D12/DXGI GPU 路径，经 Windows.UI.Xaml 面板原生接口 | [稳定帧](artifacts/d3d12-skia-probe/2026-10-28/uwp-stable-lvt.png)、[缩放后](artifacts/d3d12-skia-probe/2026-10-28/uwp-zoom-lvt.png)、[resize 后](artifacts/d3d12-skia-probe/2026-10-28/uwp-after-resize-lvt.png) |

lvt 红框/编号属于检查标注。按用户要求，后续均用 lvt 后台截图/InvokePattern，没有继续发送桌面键鼠输入。早期 computer-use 截到的被遮挡/Codex 图像不作为证据。

WPF 显式 enableSoftwareFallback=false。两个 Panel 探针为 FlipSequential、2 buffers、BGRA8、单样本、Premultiplied alpha；呈现前后恢复 D3D12 resource state。每帧有 GPU copy 和显式 fence 等待，尚未优化同步。

修复的**探针**问题：

- RenderManager 会对传入 canvas 施加 ScaleFactor，且不恢复该初始变换。持久 surface 必须每帧 Save/RestoreToCount。150% DPI 累乘曾造成“首帧有元素，随后放大白屏”；只改探针，未改核心。
- WinUI 原先缺少 PerMonitorV2 manifest，150% 屏幕上 XamlRoot scale=1、1084×749 图像被系统放大。已修复 manifest，按 CompositionScaleX/Y 分配纹理，设置交换链逆缩放并监听变化。修正后 scale=1.5，例如 718.667×478 DIP 对应约 1079×717 像素。ceil 对浮点尺寸可能带来 1 像素余量，后续需统一物理像素取整。
- UWP 首次启动 STA 错误已按 .NET 日志改为 MTA。它是本地注册、RuntimeBehavior=windowsApp、TrustLevel=appContainer 的真实 UWP，使用 .NET 10 自包含 CoreCLR，未验证 .NET Native/AOT。可见窗口由 ApplicationFrameHost 承载，lvt 按标题/句柄定位。

**读回口径**：渲染、测量和呈现循环没有 GPU→CPU 像素读回。离屏测量结束后才各编码一张诊断 PNG，可能触发一次读回。WPF 调查白屏时的首帧 target/shared11 诊断图也保留，相关读回代码已从最终 WPF 探针删除。lvt 截图来自独立验证工具。这些诊断捕获均未用于显示路径。

## 类型与样式覆盖缺口

compatibility.json 保存 369 个配方结果、实际 Plottable 类型和核心全部 67 个具体 IPlottable 的清单，plottable-coverage.csv 便于检视。

**369/369 GPU_SUBMISSION_PASS，60/67 类型被配方使用**，仅证明正常完整 Figure 在 D3D12 surface 上提交和完成；未做参考图差异比较，不能称视觉等价通过。

尚未实际绘制：Benchmark、DataStreamerXY、DraggablePlottableDecorator、HittablePlottableDecorator、IsoLines、SignalConst<T>（按开放泛型定义计）、TINPlot。

必须处理的边界：

- HittablePlottableDecorator.Render 会在后台新建 SKBitmap/SKCanvas，对 Source 再做 CPU 命中图绘制。可见 Figure 仍可走 GPU，但若禁止命中图在内的任何 Skia CPU 绘制，现有核心不满足该解释；需要明确边界或批准核心调整。本轮未修改。
- Heatmap 的 CPU 像素生成、图片解码等数据准备仍存在；GPU 目标本身没有改走 Raster surface。
- 额外 OpenGL 包的 ScatterGL 不在上述核心 67 类型中。它会把非空 GRContext 当成 OpenGL context 并调用 GL；不能填入 D3D12 context 后就声称兼容。正式宿主必须显式拒绝或在获批范围内适配，不能静默处理。本轮未转换/测试。
- Plot.GetImage/SavePng 在核心内部创建 Raster surface，探针没有接管这些原 API。GPU 导出需要获批的扩展点或明确入口；显式单次导出可读回，不能用于每帧显示。
- 自定义 IPlottable 可执行任意后端相关代码，接口相同不等于第三方实现都已兼容。
- MultiplotRecipeBase、所有字体/透明/渐变/虚线/裁剪/AA 组合、逐类型三平台视觉样本、原控件平移/Reset/导出尚未闭环。Zoom probe 只调用 Axes.Zoom，不代表原 UserInputProcessor 已接入。

## 完整 Figure 性能

Release、硬件 D3D12、ScaleFactor=1、BGRA8 单样本目标；普通 Plot.Render 每次完整重绘，预热 3 帧、记录 12 帧取中位数。最终测量前已关闭三个呈现探针。

| 工作负载 | 尺寸 | 完成时间中位数 | 显式 fence 等待中位数 | 当前线程托管分配/帧 |
|---|---:|---:|---:|---:|
| 2×1,000 Scatter + Bar/Text/轴/标题/网格/图例 | 1000×700 | 2.558 ms | 0.012 ms | 183,400 B |
| 32×20,000 普通 SignalXY + Bar/Text/轴/标题/网格/图例，AA=true | 800×600 | **159.370 ms** | 0.022 ms | **6,759,400 B** |

逐帧原始数据：offscreen.json、offscreen-dense.json。输入为 640,000 点，ScottPlot 的数据简化仍按原语义执行。

“渲染与提交”是显式 queue fence 前的墙钟时间，可能包含 Skia/驱动内部等待，不能全部解释为纯 CPU 计算。GPU timestamps、内部阻塞、原生分配、GC 暂停分布尚未测量；分配只统计当前线程，包含探针计时等开销。

没有用缓存命中代替重绘。历史曲线回读 20.077 ms、叠加层 1.75–5.21 ms 的范围与本轮全图负载不同，不给出严格加速倍数。本轮表明 GPU surface 本身不足以解决密集曲线性能，下一步应剖析原 Render/Skia path，不能自动改写完整原生 2D 引擎。

## 内存：新增验收项

### 实际窗口观察

用户实际 resize 后，第一轮 Private Bytes 约 WPF **962 MiB**、WinUI **711 MiB**、UWP **664 MiB**；后续约 31 秒/20 次稳态采样仍保持较高占用。操作没有固定尺寸/时长，不据此给出可重复的泄漏斜率。

20 秒 .NET EventCounters：

| 指标（原始计数器单位） | WPF | WinUI |
|---|---:|---:|
| GC 已提交堆 | 19.71 MB | 19.58–21.64 MB |
| GC heap 采样范围 | 2.03–13.68 MB | 2.51–15.88 MB |
| 托管分配速率均值，约 10Hz 完整重绘 | 2.37 MB/s | 2.33 MB/s |

Private Bytes 的 MiB 按 2^20 换算。Working Set、Private Bytes、GPU allocation 存在记账重叠，不能相加。

UWP EventPipe 连接未完成，已停止本轮 collector；原因未确定，缺失数据不等于 0。没有 STW trace，GC 停顿未验收。

GPU Process Memory 的 WPF Shared Usage 出现约 96 GB 异常值，不能用于实际占用结论；保留原始样本并标为不可靠。隔离实验使用进程内 IDXGIAdapter3.QueryVideoMemoryInfo。

WPF/WinUI 后台关闭后记录到 Dispose 完成。UWP 窗口关闭后进程仍存活、约 406 MiB Private Bytes，且无 uwp-closed.txt，**不能声称当前 Window.Closed 清理成立**。日志复制后，已移除本轮创建的 UWP 测试注册并释放进程；源文件和 staging 保留。正式宿主必须处理 suspend/resume/close。

### 离屏 resize 隔离实验

单独进程、普通完整 Figure：3 帧预热，重复同一组 24 尺寸共 12 轮，width=640+27*i、height=480+17*i（i=0..23），回到 800×600 绘制 30 帧。目标在 fence 后释放，无像素读回、无 GC.Collect。

| 实验 | warmup Private/GPU | 第12轮 Private/GPU | 第12轮 Skia cache | 回到800×600后 GPU |
|---|---:|---:|---:|---:|
| 默认 cache limit=256 MiB | 144.5/41.5 MiB | 367.5/**238.5 MiB** | 76.0 MiB | 236.4 MiB |
| 诊断变体：32 MiB limit + resize 后 PurgeUnlockedResources(false) | 145.1/41.5 MiB | 175.7/**43.1 MiB** | 4.9 MiB | 42.2 MiB |

默认路径在此固定尺寸集合中第1轮后形成 GPU 使用平台，不能据此断言无限泄漏。末尾额外诊断 PurgeResources 使默认 GPU 使用降至约 105.1 MiB，Private Bytes 未同步下降；工作集和 Skia cache 均不足以单独解释保留。

该实验确认缓存/分配器保留是 resize 内存成本的一部分，**未包含平台 interop、XAML、swapchain 和 compositor，不能称三个窗口已修复**。候选策略的性能代价未测量。

### 内存验收门槛（尚未通过）

1. 相同数据/DPI/固定尺寸序列，覆盖冷启动、热稳态、连续拖动、回到初始尺寸、空闲、至少20轮重复。报告 peak、settled、按轮保留变化。
2. 同时记录托管存活/已提交堆、LOH/POH、分配、GC 暂停、Private Bytes、Working Set、句柄、Skia cache、DXGI usage/budget、活动/退休资源计数。
3. 按物理像素预算：BGRA8 单纹理下界为4*W*H B；2-buffer swapchain+离屏目标至少3*4*W*H，不含 scratch/驱动粒度/compositor。WPF D3D11/D3D9 共享视图不可重复计成独立像素副本。
4. cache、in-flight frame、退休目标队列和诊断列表均须有明确上限，避免随尺寸数量增长。当前窗口探针尚无产品级约束。
5. resize 合并到渲染调度点；fence 后退休旧资源；按预算、resize结束或内存压力清理，不能未经测量就每帧 purge。
6. 验证 unload/close、UWP suspend/resume、front-buffer不可用、设备丢失、失败重试、跨DPI重建后的资源释放与恢复。不以周期性 GC.Collect 代替生命周期修复。
7. 在内存策略下重测完整 Figure 时间、等待、分配与视觉。32 MiB 是诊断变体，不是已确定的最终产品预算。

## 验证三平台后提出的共享设计

仅设计，未实现最终共享包/API。

~~~text
原 ScottPlot Plot / 原 RenderActions / 原样式
                      |
SkiaD3D12RenderSession（adapter/device/queue/GRContext）
                      |
GPU target lease（resource/backend texture/SKSurface）
                      |
Save -> Plot.Render -> Restore -> flush/submit -> fence
                      |
       +--------------+---------------+
       |              |               |
WpfD3DImageHost  WinUiSwapChainHost  UwpSwapChainHost
 GPU interop链    DXGI/原生面板       DXGI/Windows.UI.Xaml
~~~

| 所有者 | 资源与责任 |
|---|---|
| RenderSession | 设备/queue/context/cache预算；失败明确报错；最后host离开且所有fence退休后销毁 |
| Target lease | 等最后使用fence，按surface→wrapper→resource顺序释放；禁止无界保留旧尺寸 |
| WPF host | UI线程 detach backbuffer，释放On12/shared11/D3D9视图；校验相同adapter/LUID，处理front-buffer事件 |
| Panel host | UI线程 SetSwapChain(null)，退休GPU工作与backbuffer引用后 ResizeBuffers/重建 |
| UWP app/host | 明确挂起/恢复/关闭策略，不依赖当前未成立的 Window.Closed 清理 |
| Scheduler | 最新物理尺寸/DPI、刷新请求、有限in-flight与retired队列；合并过时resize |

共享层继续普通 Plot.Render，平台宿主处理目标/呈现/DPI/生命周期。已验证的 GPU copy 路径可作为正确性起点；直接包装交换链后缓冲、异步多帧等优化仍需资源状态和同步实验。

正式控件还须接入 IPlotControl/UserInputProcessor、布局和坐标换算、平移/Reset/导出等契约。本轮 Window/按钮不能被当成兼容控件。内存/性能未闭环前不扩大产品 API，不发布包。

## 文件入口与后续顺序

- dev/D3D12SkiaProbe：API、外部纹理、完整离屏图，附 README 和批量离屏复现脚本。
- dev/D3D12SkiaWpfProbe：独立 D3DImage 窗口。
- dev/D3D12SkiaWinUiProbe：独立 WinUI 3 面板和 PerMonitorV2 manifest。
- dev/D3D12SkiaUwpProbe：现代 UWP AppContainer、manifest和assets，本地测试注册已清理。
- dev/D3D12SkiaCompatibilityProbe：369配方和全类型清单。
- dev/D3D12SkiaMemoryProbe：resize缓存隔离实验。
- artifacts/d3d12-skia-probe/2026-10-28：JSON、日志、截图、固定版本源码、DLL导出和哈希；被原仓库规则忽略。

下一轮：完整Figure时间/分配剖析与内存预算 → 三平台受控resize/挂起/关闭 → 未覆盖类型/全样式视觉基准 → 原控件API/交互 → 设备丢失/DPI/多适配器 → 全部通过后干净staging重新打包。未获批准不修改核心、不重写完整原生2D引擎。




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
