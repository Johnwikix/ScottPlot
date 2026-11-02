# 2026-11-02 Avalonia 可选后端实现与验证

实现了统一 `AvaPlot` API 下的可选 Windows D3D12 provider。跨平台基础包默认走 Avalonia 的现有 Skia canvas；Windows 启动项目引用独立 provider 并注册后，控件可以显式选择 Direct3D12。失败不切换后端。

## 接入

共享 UI 项目引用 `ScottPlot.Avalonia`，使用 `net8.0` / `net10.0`。Windows 启动项目另外引用 `ScottPlot.Avalonia.D3D12`，使用相应的 `-windows` 目标框架：

```csharp
using ScottPlot.Avalonia;
using ScottPlot.Avalonia.D3D12;

AppBuilder.Configure<App>().UsePlatformDetect().UseScottPlotD3D12();

plotControl.RenderBackend = AvaPlotRenderBackend.Direct3D12;
await plotControl.RefreshAsync();
```

Linux/macOS 保持默认 PlatformSkia，不需要 Windows provider。`RenderBackend` 可从共享 XAML 绑定，应用在平台配置处决定其值。

`RefreshAsync` 将原始初始化/呈现错误交给调用者。原有 void `Refresh` 合并持续更新请求，避免渲染速度落后时形成无界队列；错误通过 `RenderException`、`RenderFailed` 和控件内的错误面板明确显示。错误锁存后不会自动重试，调用 `RetryRenderAsync` 或明确选择 PlatformSkia 才恢复。

两条路径使用相同逻辑像素坐标。D3D12 按窗口 DPI 创建物理输出，保留用户 Plot.ScaleFactor。设备按 compositor LUID 匹配，并在同一 UI 线程/显卡上的 D3D12 控件之间共享。detach 取消旧帧并释放后端；重新挂载创建新实例。永久移除时可 await DisposeAsync；并发释放等待同一任务。

## 实际检查

- Unit Tests：**498/498 通过**，其中新增 12 个后端策略、失败、生命周期、刷新合并与 DPI 回归用例。
- 基础包：net8.0、net10.0 编译通过。
- Windows provider：net8.0-windows、net10.0-windows 编译通过，无警告。
- 新建独立包消费目录与 NuGet 缓存，只使用生成的 nupkg，未引用源码项目；平台中立的共享项目和两个 Windows 目标框架均编译通过。
- 两个 NuGet 包消费程序分别在 .NET 8 和 .NET 10 下各通过 **25 项真实窗口检查**。
- 共享项目的解析依赖中没有 ScottPlot.D3D12.Core、ScottPlot.Avalonia.D3D12 或 Vortice。
- 真实软件合成器、未注册 provider 两个进程均报告明确异常，保持 Direct3D12 选择且未执行 Skia 回退绘制。

Windows 实测为 Intel Arc 140T、Avalonia 12.0.0、SkiaSharp 解析版本 3.119.3-preview.1.1；实际跨屏 DPI 为 150% 与 125%。SkiaSharp 版本来自 Avalonia 的传递依赖，新增项目没有引入与仓库不同的显式 SkiaSharp 版本。

硬件窗口检查覆盖：

1. Skia/D3D12 切换前后的 GetPixel 坐标相同，Plot.ScaleFactor=1.4 未被 DPI 覆盖。
2. 12 次控件尺寸变化及实际跨屏 DPI，物理输出与逻辑 FigureRect 分别正确。
3. 实际 DPI 下的 UserInputProcessor 缩放锚点。
4. 4 次卸载/挂载重建和反复后端切换。
5. 初始化失败、真实 GPU 渲染流程中的注入异常、错误锁存及显式重试。
6. Reset 的 PlotControl 关联与完整 Multiplot 渲染。
7. 连续正常呈现的 PixelReadbackCount=0；随后一次明确 CaptureBitmap 导出后计数为 1。

lvt 使用 WindowPattern 和 InvokePattern 在后台检查，记录 HasKeyboardFocus=false。保存了正常窗口、缺少 provider、软件合成器、运行时错误以及 Retry 后恢复的真实窗口截图。没有通过前台 SendInput 注入鼠标操作。

## 本地包与证据

包目录：`artifacts/avalonia-backend/2026-11-02/packages`：

- `ScottPlot.Avalonia.5.1.61-gpu.1.nupkg`
- `ScottPlot.Avalonia.D3D12.5.1.61-gpu.1.nupkg`
- `ScottPlot.D3D12.Core.5.1.61-gpu.1.nupkg`
- 从当前源码打包的 `ScottPlot.5.1.60.nupkg`，用于独立包消费验证。

新版本采用 5.1.61-gpu.1，防止 NuGet 将依赖解析到不含新 API 的 5.1.60 基础包/Core。包仅生成于本地，没有远程发布。

主要证据目录：

- `unit-tests-release/unit-tests.trx`：498 个测试。
- `package-runtime-net8.0-windows`、`package-runtime-net10.0-windows`：各自 environment、validation、state、窗口截图、GPU 导出与 shutdown。
- `no-provider`、`software`：实际不可用条件下的 expected-failure 和 lvt 窗口截图。
- `release-validation`：lvt 后台注入运行时故障与显式 Retry 的前后截图。
- `package-smoke/common`、`package-smoke/windows`：独立包消费项目与解析后的依赖。

完整复现入口为此目录的 Run-Validation.ps1。正常渲染未执行 CPU 像素读回；composition 诊断截图和 CaptureBitmap 明确在正常呈现循环之外。Avalonia 可以额外执行 GPU snapshot copy，此实现不宣称零拷贝或所有 Skia 内部算法都纯 GPU。

该脚本也已实际执行：`-NoBuild -Close` 的硬件、未注册 provider、软件合成器三种模式全部通过，证据在 `script-release`；各窗口均由 lvt 后台截图并关闭。

## 验证限制

全解决方案工作负载恢复受到本机权限限制，restore 指出缺少 wasm-tools-net10、maui-tizen，full build 还遇到 WinForms 的 SkiaSharp 引用问题。因此没有声称全解决方案构建通过；本次库、包消费及 Unit Tests 已分别验证。

Linux/macOS 未在实机运行；已验证基础包没有 Windows 目标或 D3D12 依赖，默认 Skia 路径通过平台中立的 headless 测试。多显卡、真实设备丢失恢复与长期 GPU 内存压力尚未在本机完整验收。当前版本是具有正式包结构和统一 API 的本地 prerelease。

本轮没有重新执行原探针的完整性能基准，不把旧探针的帧时间作为正式统一控件的新性能结果。
