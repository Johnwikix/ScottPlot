# 2026-11-02 Windows Avalonia D3D12 探针结果

本机结论：D3D12 全图渲染可以通过 D3D11 keyed-mutex 共享纹理进入 Avalonia 合成树，无需应用逐帧像素读回。Windows 可选后端路线可行；此结果尚不构成正式控件包的全平台/全样式验收。

## 环境与证据

- Windows 10.0.26300，x64，.NET 10.0.14，Intel Arc 140T。
- Avalonia 12.0.0；SkiaSharp 包 3.119.3-preview.1.1（程序集版本 3.119.0.0）。
- Avalonia 后端：ANGLE/OpenGL，软件 fallback 禁用；图表 D3D12 后端：现有 Core 的 Direct3D GPU surface、原生 MSAA4。
- 显示器实际 DPI：150%、125%；正式性能采样固定于 150%。
- 场景：800×600 DIP / 1200×900 物理像素，32×20,000 SignalXY，加 Bars、Text、标题、坐标轴、网格与图例，线宽 2、AA。
- 正式证据目录：`artifacts/avalonia-d3d12-probe/2026-11-02/final`。

正式运行通过 31 项自动检查。受控 100%/125%/150%/200% render scale 下，纹理尺寸与物理像素换算一致，缩放前后鼠标锚点坐标一致；6×4 次尺寸变化全部完成；实际窗口跨屏后分别呈现 1200×900 与 1000×750 输出。

缩放通过 UserInputProcessor 的 wheel action 测试。按照后台检查要求，没有前台鼠标输入注入；原生 pointer-event 注入不是本轮验收内容。

lvt 的后台 WindowPattern 最大化/恢复和 InvokePattern 后端切换均成功，记录的 HasKeyboardFocus=false。最大化后图表实际输出 2844×1557，恢复后重新匹配窗口尺寸。真实窗口截图确认文字方向、轴、图例以及 Avalonia overlay 正常；修正了初始导入未声明 TopLeftOrigin 导致的上下翻转。

## 完整帧性能

每组预热 10 帧、测量 60 帧、3 轮；每个单元格来自 180 帧。全部 1,080 个测量帧恰好完成一次完整 Figure 重绘，无缓存命中冒充重绘，应用呈现读回计数增量之和为 0。

`Complete` 为请求/数据更新至对应 CompositionBatch.Rendered，再等待相同 DwmFlush barrier 的墙钟耗时，单位 ms：

| 后端 | 静态中位数 | 静态 P95 | 更新中位数 | 更新 P95 |
|---|---:|---:|---:|---:|
| 现有 AvaPlot | 449.77 | 590.69 | 424.73 | 574.98 |
| D3D12，关闭信号优化 | 41.63 | 50.07 | 41.75 | 50.08 |
| D3D12，开启现有信号优化 | 32.68 | 33.77 | 33.29 | 34.84 |

对应的 composition 完成中位数，不含后续 DwmFlush：

| 后端 | 静态 | 原位更新 |
|---|---:|---:|
| 现有 AvaPlot | 441.62 | 419.81 |
| D3D12，关闭信号优化 | 34.68 | 34.86 |
| D3D12，开启现有信号优化 | 25.42 | 26.43 |

相对此机器上的现有 AvaPlot，本次配置下普通 D3D12 完整帧约快 10 倍，开启信号优化后约快 13 倍。这些数字包含合成/同步；不能使用 renderer 内部约 16–17 ms 的结果代替完整帧约 33 ms。

限制：D3D12 使用 MSAA4，AvaPlot 沿用 Avalonia 的 AA 配置，sample count 未强制一致；suite 的 resize 验证先于性能测量，renderer 保留增长后的 GPU 目标容量。该比较反映实际控件配置，不是严格隔离图形 API 的微基准。结果不代表所有 GPU、所有绘图类型或多帧流水线吞吐；DwmFlush 也不是屏幕扫描完成时间戳。

## 读回与呈现审计

`benchmark.json` / `frames.csv` 保存每帧重绘数和读回增量。`validation.json` 在最后一项检查 GPU copies、已完成的 Avalonia updates 与渲染帧数相等。正常呈现只执行 GPU copy；一次性 composition 截图读回单独记录，位于计时循环之外。

Avalonia 12 的 ANGLE imported-image 实现采用 GPU snapshot copy，因此本方案不声称零拷贝或合成阶段零分配。Skia 内部所有算法是否完全无 CPU raster path 未做穷尽追踪。

其他主要证据：

- `environment.json`、`validation.json`、`benchmark.json`、`frames.csv`。
- `monitor-1.50.png`、`monitor-1.25.png`，以及四种受控 render scale 截图。
- `d3d12-optimized-lvt.png`、`avaplot-lvt.png`、`maximized-lvt.png`、`restored-lvt.png`。
- `maximized-interaction.json`、`restored-interaction.json`、`window-uia.json`。
- `hashes.json`、`resolved-packages.json`、`readback-source-audit.txt`。

## 构建与复现

独立探针 Release 构建通过，未修改既有生产源文件。构建中的两个 NU1902 来自原有 Microsoft.Build.Tasks.Git 8.0.0 依赖。本轮没有改动 ScottPlot 算法或既有控件，不重复执行不受影响的全解决方案/单元测试。

`Run-Probe.ps1 -NoBuild -Frames 3 -Warmup 1 -Rounds 1` 也已实际执行成功，验证了结果检查、包版本/哈希记录与 lvt 自动截图。该快速复现的结果在 `2026-11-02/script-check`，不与正式 1,080 帧结果混合。

正式化前仍需覆盖设备丢失恢复、多 GPU 选择、长期内存、透明混合和完整样式矩阵。当前探针不发布包，不自动切换 CPU，也不改变现有 AvaPlot 默认后端。
