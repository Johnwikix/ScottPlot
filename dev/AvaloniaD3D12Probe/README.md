# Windows Avalonia D3D12 呈现探针

独立 `dev` 项目，复用当前 `ScottPlot.D3D12.Core`；不修改 ScottPlot 核心、现有 AvaPlot、平台控件或解决方案，也不生成/发布 NuGet。目标环境是 Windows x64、.NET 10、Avalonia 12.0.0。

## 运行

在此目录执行：

```powershell
./Run-Probe.ps1
```

默认每组预热 10 帧、测量 60 帧、运行 3 轮，合计 1,080 个测量帧。输出到仓库 `artifacts/avalonia-d3d12-probe/日期/时间`。脚本拒绝复用非空输出目录；窗口保留，方便用 lvt 后台检查。仅快速复现可使用 `-Frames 6 -Warmup 2 -Rounds 1`。

依赖版本遵循 Avalonia 12.0.0：探针的托管 SkiaSharp 和 Win32 native assets 均解析到 `3.119.3-preview.1.1`；现有 Core 的项目引用仍为 3.119.0。运行结果仅证明本次解析版本组合，不改变生产包的依赖声明。

## 呈现和同步

```text
普通 Plot/Multiplot
→ 现有 Skia D3D12 renderer / 原生 MSAA4 / 显式 Resolve
→ D3D11On12 包装已 resolve 的源纹理
→ GPU copy 到 D3D11 BGRA8 / SharedKeyedMutex 输出纹理
→ Avalonia ImportImage / TopLeftOrigin=true
→ UpdateWithKeyedMutexAsync / CompositionDrawingSurface
→ Avalonia 合成器
```

原生 D3D11 设备创建共享 mutex 纹理，11On12 设备打开它并负责 GPU copy。生产者获取 key 0、释放 key 1；Avalonia 获取 key 1、释放 key 0。等待消费完成后才复用或释放纹理。显式检查 mutex acquire 返回值为 S_OK，不能把正值 WAIT_TIMEOUT 当成成功。

检查 Avalonia 合成器和现有 D3D12 核心的 adapter LUID 一致；当前核心选择第一个硬件适配器，不匹配时探针明确失败。ANGLE 是本次唯一允许的 Avalonia 后端，禁止软件 fallback。`BackendWitness` 从真实窗口的 Skia lease 记录 GRContext；D3D12 核心另外检查 Direct3D context、GPU surface 和原生 MSAA4。

这条链包含 GPU copy。Avalonia 12 的 ANGLE 导入实现还会通过 `CopyTexSubImage2D` 创建 GPU snapshot，所以不能称为零拷贝，也不能推断合成阶段零分配。

## 无逐帧读回的证据

- 正常循环仅使用 default GPU textures，D3D11 输出 `CPUAccessFlags.None`；没有 staging texture、Map、ReadPixels、Plot.GetImage 或 CaptureBitmap 调用。
- 每帧检查现有 renderer 的 `PixelReadbackCount` 增量为 0；逐帧记录在 `frames.csv` 与 `benchmark.json`。每帧另外断言完整 Figure 的 RenderCount 增加，防止缓存画面冒充重绘。
- 记录 D3D11 GPU copy 与 Avalonia 消费完成次数，验证两者相等。
- `CreateCompositionVisualSnapshot` 的明确单次诊断读回仅发生在截图步骤，全部位于计时循环之外。lvt 同样在测量结束后截图。
- Avalonia 的 `GlSkiaImportedImage.SnapshotWithKeyedMutex` 路径使用 GPU texture copy；此处的 snapshot 不代表 CPU 像素读回。

这些证据覆盖正常呈现循环。没有声称布局、字体准备、路径构建等工作全部在 GPU，也没有穷尽跟踪 Skia 原生内部的每一种算法。

## 验证范围

- 完整 Figure：32 条 × 20,000 点 SignalXY，线宽 2、AA，Bars/Text/坐标轴/网格/标题/图例，以及覆盖在图表上的 Avalonia 内容。
- 100% / 125% / 150% / 200% 受控 render scale：检查物理纹理尺寸、鼠标坐标换算，以及缩放前后鼠标锚点坐标相等。这与真实显示器 DPI 检查分别记录。
- 6 轮 × 4 种大小：640×420、960×540、321×257、800×600 DIP。每次重新布局并呈现；在生产者/消费者同步之后销毁旧导入纹理。
- 实际窗口移至各显示器，检查 `Screen.Scaling`、窗口 `RenderScaling` 与物理纹理尺寸，并输出各显示器截图。
- 切换现有 AvaPlot / D3D12 / D3D12 optimized。离开可视树后，已排队的自动刷新不能访问空的 composition visual。
- 测量结束进入可拖动尺寸的窗口模式；lvt 可用 WindowPattern 在后台最大化/恢复，并通过 InvokePattern 切换后端。

## 性能口径

三个后端使用独立且相同的普通 Plot 与数组数据：

1. `avaplot`：仓库原有 AvaPlot，通过 Avalonia 提供的 Skia canvas 绘图。
2. `d3d12`：现有 D3D12Renderer，关闭 OptimizeSignals，保留普通 render actions。
3. `d3d12-optimized`：开启现有采样/路径/缓冲优化。

每轮包含静态完整重绘和全部 Y 原位更新两种场景；更新包含在计时内。轮次反转后端顺序以降低固定执行顺序的影响。

组合 suite 先执行 resize/DPI 验证，再测性能。现有 renderer 会保留增长后的 GPU 目标容量，因此结果包含其容量策略带来的 resolve 成本，不是每次都重新创建恰好等于视口大小的目标。

`CompositionMs`：从请求重绘/开始数据更新，到包含该帧的 `CompositionBatch.Rendered`。两条路径均捕获同一帧的 batch，不在帧结束后额外请求空 batch。

`CompleteMs`：上述过程后再经过相同的 `DwmFlush` barrier，包括后台线程调度和 DWM/vsync 等待。这是可复现的完整帧墙钟口径，不是 GPU timestamp，也不是显示器像素响应/扫描完成的输入延迟。单帧串行测量不代表多帧流水线吞吐上限。

`DrawMs`：ScottPlot 完整 render events 之间的 CPU 墙钟时间；D3D12 的 `RendererMs` 还包含 resolve 与真实 GPU fence 等待。不能用它们代替 CompleteMs。

D3D12 使用原生 MSAA4；AvaPlot 使用 Avalonia/ANGLE 的现有 AA 配置，没有强制设置相同 sample count。因此比较表示本次实际控件配置的差异，不是严格隔离图形 API 的微基准，也不证明所有风格逐像素等价。

## 证据和限制

`environment.json` 记录机器和实际显示器；`validation.json` 记录各项检查；`benchmark.json` 和 `frames.csv` 保留全部帧；`hashes.json`、`resolved-packages.json` 用于复现。故障写入 `failure.txt` 或 `fatal.txt`，失败目录不得作为验收证据。

这是 Windows 可行性探针，不是正式 Avalonia GPU 控件包。尚未覆盖设备丢失恢复、多 GPU、长期内存压力、所有 plottable/样式、透明混合以及其他平台。现有渲染器每帧等待 GPU idle，呈现使用单个同步共享输出；后续可以在保持同一验收口径下评估多缓冲和 fence 流水线。

官方接口与实现参考：

- [Avalonia 12 D3D11 示例](https://github.com/AvaloniaUI/Avalonia/blob/12.0.0/samples/GpuInterop/D3DDemo/D3D11Swapchain.cs)
- [Avalonia 12 外部图像属性](https://github.com/AvaloniaUI/Avalonia/blob/12.0.0/src/Avalonia.Base/Platform/PlatformGraphicsExternalMemory.cs)
- [Avalonia 12 GPU texture snapshot 实现](https://github.com/AvaloniaUI/Avalonia/blob/12.0.0/src/Skia/Avalonia.Skia/Gpu/OpenGl/GlSkiaExternalObjectsFeature.cs)
- [DwmFlush](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/nf-dwmapi-dwmflush)

2026-11-02 的实际运行结果见 [RESULTS.md](RESULTS.md)。
