# ScottPlot.D3D12 全图 GPU 后端需求与交接

日期：2026-10-28  
仓库：`D:\code\classLib\ScottPlot\ScottPlot`

## 1. 目标

提供一个完整的 **ScottPlot 全图 D3D12 GPU 渲染后端**。

所有 ScottPlot 绘图类型和图表元素都必须通过该后端渲染，包括：

- 所有 Plottable，而非仅 SignalXY 或其他曲线类型。
- 坐标轴、刻度、网格、标题、图例、标注、边框和背景。
- 文字、路径、填充、图片、标记、渐变、透明度、虚线、裁剪和抗锯齿等现有样式能力。
- 现有布局、缩放、平移和其他交互行为。

“原样支持”指保留现有 ScottPlot API、绘图语义、样式和视觉结果，不能通过另一套只支持部分图类型的 API 替代。

## 2. 渲染路线

第一阶段采用 **Skia GPU 可行性验证**：

1. 检查当前 Skia/SkiaSharp 3.119.0 是否提供受支持的 D3D12 GPU context 和 GPU-backed SKSurface。
2. 验证能否将 D3D12 texture 包装为 SKSurface，并直接执行完整的 ScottPlot `Plot.Render`。
3. 如果可行，保留现有 SKCanvas 绘图语义，使所有 Plottable 无需逐个重写即可使用 D3D12 GPU。
4. 如果不可行，停止实施并报告缺失的 API、原生符号或互操作能力，以及可选路线。不得未经批准开始重写 ScottPlot 核心或完整原生 2D 引擎。

允许使用 Skia 作为 GPU 绘图 API，但必须满足：

- 实际渲染后端是 D3D12 GPU。
- 不使用 Skia CPU Raster surface 冒充 GPU 渲染。
- 不在 D3D12 包内提供 Skia CPU 软件回退。
- 不通过每帧 GPU→CPU 像素读回完成显示。
- 初始化或运行时不满足 GPU 条件，应明确失败并报告，不能静默切换到 CPU。

用户不需要 GPU 加速时，移除 `ScottPlot.D3D12` 包并继续使用原 ScottPlot 即可。

## 3. 平台要求

共享同一个 D3D12 渲染核心，并分别提供平台呈现宿主：

| 平台 | 呈现方式 |
|---|---|
| WPF | D3DImage 及所需的 GPU 互操作链 |
| WinUI | 原生 DXGI/D3D12 交换链和 SwapChainPanel |
| UWP | 原生 DXGI/D3D12 交换链和 SwapChainPanel |

三个平台都必须有真实可运行的验证。仅添加目标框架、项目文件或依赖声明不算支持完成。

## 4. 当前实施范围

- 第一阶段只做技术验证和最小原型。
- 未获得明确批准前，不修改 ScottPlot 核心源码。
- 不修改 DisplayApp 生产代码。
- 不主动创建 git commit。
- 不发布到远程 NuGet 源。
- 开始工作前检查当前工作区状态，保留已有修改，不 reset 或覆盖未提交内容。

## 5. 已有成果与路线调整原因

### 5.1 最初问题

DisplayApp 原有 D3D12 叠加层显著提高了曲线性能，但出现过两个正确性问题：

- 缩放后的曲线出现类似虚线的几何断裂。
- 全控件叠加层遮挡 ScottPlot 已有图表元素。

因此开始验证：能否利用 ScottPlot 的公开渲染扩展点，在不修改核心的情况下，将 GPU 曲线纳入正常的图表顺序和 DataRect 裁剪。

### 5.2 已验证的独立曲线包

仓库中已有独立项目：

- `src/ScottPlot5/ScottPlot5 Controls/ScottPlot.D3D12`
- `src/ScottPlot5/ScottPlot5 Tests/D3D12 Tests`

该实现已经证明以下能力可行：

- 通过公开 `RenderManager.RenderActions` 替换 plottable 渲染阶段，无需修改 ScottPlot 核心。
- `D3D12SignalXY` 和 `plot.UseD3D12()` 可以将合格曲线交给 D3D12。
- 支持原生 4× MSAA、单样本渲染、真实 miter/StrokeMiter/bevel 几何和 DataRect 裁剪。
- 连续曲线可以合批，普通 ScottPlot 元素仍能保持原渲染顺序。
- 同一数据点数下原位修改数据也能重新渲染，不依赖 Count 缓存。
- 超大上传长度在分配和写顶点前进行检查。

这些成果证明了 D3D12 设备、几何、MSAA 和 ScottPlot 渲染扩展点的可行性，但只覆盖曲线，不代表全图 GPU 后端已经完成。

### 5.3 已完成的真实验证

曲线回读包此前取得以下结果：

- D3D12 扩展测试：13/13 通过。
- 上游 ScottPlot Unit Tests：486/486 通过。
- 覆盖 AA 开关、DataRect 裁剪、plottable 顺序、ScaleFactor 1/1.5/2、同点数改值、密集曲线连续性、轴缩放、锐角 miter 外轮廓和虚线软件回退。
- 32 曲线 × 20,000 点的 WPF Sandbox 已做稳定帧截图和真实滚轮放大，确认曲线连续，标题、网格、刻度和边框层级正确。
- net8/net10 NuGet 和符号包曾成功生成，未发布到远程源。

测试使用 GPU/回退计数增量进行断言，避免早期成功帧掩盖后续静默软件回退。这是后续全图后端测试仍应保留的原则。

### 5.4 性能结果与为什么停止读回路线

测试口径：32×20,000、800×600、Release、AA=true，预热 3 次，每轮 12 次取中位数；测量期 GPU 12/12、回退 0。

| 路径 | 代表结果 |
|---|---:|
| Skia CPU | 229.206 ms |
| 曲线包：4× MSAA + 同步读回 + SKCanvas 合成 | 20.077 ms |
| DisplayApp 原 D3D12 叠加层，单线程 | 4.62–5.21 ms |
| DisplayApp 原 D3D12 叠加层，并行 | 1.75–1.89 ms |
| 原叠加层缓存命中 | 约 0.008 ms |

完整结果保存在：

`D:\code\wpf\DisplayApp\artifacts\chart-performance\scottplot-d3d12-msaa-readback-2026-10-28.json`

曲线包虽然相对 Skia CPU 快约 11.42×，但仍明显慢于原叠加层。主要成本是 D3D12 纹理无法直接由普通 SKElement 消费，需要同步 GPU→CPU 读回、逐行复制和 SKCanvas 合成。

因此，新需求明确转向完整 GPU surface 和平台原生呈现，不再把逐帧读回作为最终方案。上述历史数据只用于说明路线选择，不是新全图后端的性能结果。

### 5.5 已验证的互操作经验

DisplayApp 原型已验证过以下呈现链：

```text
D3D12
→ D3D11On12
→ D3D11 shared texture
→ D3D9Ex
→ WPF D3DImage
```

关键经验：

- D3D11 共享纹理必须带 `ResourceOptionFlags.Shared`，否则 D3D9 可能打开无关纹理，出现“纹理内容正确但窗口不显示”。
- D3D9Ex 通过 `CreateTexture(..., ref sharedHandle)` 和 `GetSurfaceLevel(0)` 打开共享纹理。
- D3D11On12 的 COM 签名和 vtable 委托必须以 Windows SDK 头文件为准，不能凭记忆修改。
- 独立调用 ScottPlot 像素提取前必须设置 `signal.Axes.DataRect`。
- 显示链是否正确必须通过真实窗口截图验证，不能只检查纹理快照。

这些经验可用于 WPF 呈现探针，但 WinUI/UWP 仍需要分别验证其 SwapChainPanel 生命周期和交换链互操作。

## 6. 当前代码和测试状态

### 6.1 未完成的 WPF 曲线宿主

`WpfPlotD3D12` 三层曲线宿主的源码已经加入，`ScottPlot.D3D12.csproj` 的 net10 Release 构建曾通过，但该宿主：

- 从未实际运行。
- 没有窗口截图、缩放、Reset、导出或生命周期验证。
- 没有性能结果。
- 只处理曲线，其他元素仍由 Skia 绘制，并含软件回退。

它是当前工作区状态的一部分，不是新全图后端已经可用的证据。

### 6.2 Sandbox 中断状态

用户暂停工作时，Sandbox 切换尚未完成：

- `MainWindow.xaml` 的命名空间已改为 `ScottPlot.D3D12`，但标签仍是 `WpfPlot`。
- `MainWindow.xaml.cs` 已删除 `D3D12Registration` 字段，但仍保留旧赋值和 Dispose。

因此不要假设当前 Sandbox 可以构建。接手时先检查实际 diff，并做最小修复或隔离，不要覆盖其他未提交修改。

### 6.3 如何解释已有测试

- 13/13 和 486/486 是曲线回读实现当时的结果。
- 后续共享代码已经变化，当前源码必须重新运行才能再次声称通过。
- 这些测试不覆盖全 Plottable D3D12、Skia GPU surface、WinUI 或 UWP。
- 新后端需要新增 GPU-backed surface 判定、无 CPU 回退、无逐帧读回、全图样式和三平台真实呈现测试。
- 如果测试发生静默 CPU 回退，应判定为失败，不能只凭截图相似判定 GPU 成功。

### 6.4 构建产物

现有 `ScottPlot.D3D12.5.1.60.nupkg` 是旧曲线实现生成的产物，不是新全图 GPU 后端交付物。任何新包都必须从当前源码和干净输出重新生成。

### 6.5 基线复现命令

在仓库根目录执行：

```bash
dotnet build "src/ScottPlot5/ScottPlot5 Controls/ScottPlot.D3D12/ScottPlot.D3D12.csproj" -f net10.0-windows -c Release --no-restore

dotnet test "src/ScottPlot5/ScottPlot5 Tests/D3D12 Tests/ScottPlot.D3D12.Tests.csproj" -c Release --filter "FullyQualifiedName~RenderingTests"

dotnet test "src/ScottPlot5/ScottPlot5 Tests/Unit Tests/ScottPlot Unit Tests.csproj" -c Release
```

旧曲线回读性能测试是 Explicit，仅用于复现历史基线：

```bash
dotnet test "src/ScottPlot5/ScottPlot5 Tests/D3D12 Tests/ScottPlot.D3D12.Tests.csproj" -c Release --filter "FullyQualifiedName~MeasureCpuAndGpuReadback"
```

## 7. 新技术验证顺序

### 7.1 SkiaSharp D3D12 能力

- 核对公开 .NET API、原生库和导出符号。
- 记录准确 API、包版本、目标框架、处理器架构和测试结果。
- 不因为 Skia 原生仓库包含 D3D12 代码，就推断 SkiaSharp 已公开对应绑定。

### 7.2 完整 Figure 离屏探针

建立以下最小链路：

```text
D3D12 device/queue
→ Skia D3D12 GPU context
→ D3D12 texture 包装的 GPU-backed SKSurface
→ 普通 ScottPlot Plot.Render
```

探针必须：

- 使用普通 ScottPlot Plot，而不是某个 GPU 专用 Plottable。
- 同时包含曲线、非曲线 Plottable、文字、坐标轴、网格和图例。
- 证明 SKSurface 是 GPU-backed，而不是 Raster surface。
- 证明没有 CPU 软件回退和逐帧像素读回。

### 7.3 平台呈现探针

离屏探针成功后，分别完成：

1. WPF D3DImage 真实窗口显示。
2. WinUI SwapChainPanel 真实窗口显示。
3. UWP SwapChainPanel 真实窗口显示。

先验证各平台的实际互操作和生命周期，再提取共享抽象，避免基于未验证假设设计跨平台 API。

## 8. 验收要求

### 8.1 绘图兼容性

- 覆盖 ScottPlot 现有 Plottable 和图表装饰，而非只测试曲线。
- 检查文字和字体、路径描边与填充、标记、图片、透明度、渐变、虚线、裁剪和抗锯齿。
- 检查布局、图例、标题、网格、坐标轴、缩放、平移和交互。
- 对不支持的任何能力明确列出，不能静默省略或改走 CPU。

### 8.2 呈现和生命周期

- 使用真实窗口截图验证首帧、稳定帧和交互后的画面。
- 验证 DPI、尺寸变化、交换链或后缓冲重建、设备丢失和资源释放。
- 纹理快照、构建成功或没有异常不能单独证明显示链正确。
- 未实际运行的平台和场景必须标为未验证。

### 8.3 性能

- 性能测量必须针对完整 Figure。
- 记录渲染后端、分辨率、DPI、Plottable 类型、数据规模和测量口径。
- 记录分配、同步等待以及是否存在 GPU→CPU 读回。
- 缓存命中结果不得冒充完整重绘性能。
- 历史曲线数据只作路线对照，不得作为全图后端结果。

## 9. 交付要求

- 每轮记录已验证内容、证据、失败项和未验证内容。
- 如果 Skia GPU 路线不可行，先提交调查结论，不直接更换成其他大型实现。
- 全部目标验证完成后，才从当前源码重新生成 NuGet。
- 打包前使用干净的 staging/输出目录，禁止复用已有二进制或旧 nupkg。

## 10. 可直接交给另一个 agent 的 prompt

```text
请在 D:\code\classLib\ScottPlot\ScottPlot 中验证并设计 ScottPlot.D3D12 全图 GPU 后端。先完整阅读仓库根目录 D3D12-NATIVE-WPF-HANDOFF.md，并检查当前工作区状态；保留已有修改，不要 reset 或覆盖。

目标：所有 ScottPlot Plottable、坐标轴、网格、标题、图例、标注和现有样式保持原 API、布局和视觉语义，并实际通过 D3D12 GPU 渲染，而不是只加速某种曲线。最终共享渲染核心需要支持 WPF D3DImage，以及 WinUI/UWP 的原生 DXGI/D3D12 交换链和 SwapChainPanel。

第一阶段只做 Skia GPU 技术验证和最小原型：
1. 检查 Skia/SkiaSharp 3.119.0 的公开 API、原生库和符号，确认能否创建 D3D12 GPU context，并将 D3D12 texture 包装成 GPU-backed SKSurface。
2. 如果可行，使用普通完整 ScottPlot Plot.Render 做离屏探针，画面必须包含曲线、非曲线 Plottable、文字、坐标轴、网格和图例。
3. 证明实际使用 D3D12 GPU，不是 Skia CPU Raster surface，没有软件回退，也没有每帧 GPU→CPU 像素读回。
4. 离屏探针成功后，分别做 WPF、WinUI、UWP 的真实窗口呈现探针，再考虑共享抽象。

允许 Skia 作为 GPU 绘图 API，但最终 D3D12 包不需要 Skia CPU fallback。用户不想要 GPU 加速时会移除整个扩展包。GPU 条件不满足时必须明确失败，不能静默切换 CPU。

已有曲线回读包曾通过 13/13 扩展测试和 486/486 上游测试，并有真实 WPF 截图与滚轮缩放验证；其代表性能为 20.077 ms，慢于原 D3D12 叠加层的 1.75–5.21 ms，主要瓶颈是同步读回。它们只证明曲线与互操作经验，不代表全图后端完成。当前 WpfPlotD3D12 只编译过、未运行，Sandbox 还有未完成切换；先检查并最小修复或隔离，不要覆盖现有修改。现有 nupkg 是旧产物。

如果 SkiaSharp 没有所需 D3D12 绑定，或 GPU surface/平台互操作不可行，请停止实施并报告准确证据和备选路线。不要静默退回 CPU，不要未经批准修改 ScottPlot 核心，也不要自动开始重写完整原生 2D 引擎。

验收时必须覆盖所有绘图类型和样式能力，并用真实窗口截图验证三个平台。性能只测完整 Figure，明确记录后端、数据规模、同步等待、分配和读回情况。未实际验证的项目必须明确标注。

不要修改 DisplayApp 生产代码，不要主动 commit，不要发布远程 NuGet。全部验证完成后才从干净输出重新打包。
```
