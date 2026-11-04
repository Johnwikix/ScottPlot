# WinForms D3D12 实现与测试交接

日期：2026-11-04（Asia/Tokyo）。仓库：`D:\code\classLib\ScottPlot\ScottPlot`。

## 1. 交付范围和本次状态

新增独立项目/包 `ScottPlot.D3D12.WinForms`，控件为 `ScottPlot.D3D12.FormsPlotD3D12`，目标为 `net8.0-windows`、`net10.0-windows`。
控件继承 `ScottPlot.WinForms.FormsPlotBase`，使用现有 Plot、Multiplot、Reset、UserInputProcessor 和光标 API。
完整 Figure（曲线、其他 Plottable、坐标轴、文字、图例、背景）复用现有 Skia D3D12 核心，原生 MSAA/resolve 后经 GPU 复制到 HWND 交换链。
交换链为 BGRA、双缓冲、FlipDiscard、单样本；MSAA 在核心离屏纹理上完成。普通 WM_PAINT 不调用 CaptureBitmap。
HWND 接入依据：[Microsoft D3D12 swap chains](https://learn.microsoft.com/en-us/windows/win32/direct3d12/swap-chains)、[CreateSwapChainForHwnd](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/nf-dxgi1_2-idxgifactory2-createswapchainforhwnd)。

新增公开 `D3D12HwndPresenter`，因此 Core 和 WinForms 项目版本为 `5.1.61-gpu.2`，避免新控件依赖缺少该类型的旧 Core 包。
解决方案已加入新控件项目；两个既有打包脚本已加入 WinForms 和普通 WinForms 依赖。脚本未执行，未发布 NuGet。

本次完成的检查：

| 检查 | 状态 |
| --- | --- |
| WinForms 库 net8/net10 Release 编译 | 通过，零警告/零错误 |
| WinForms 验证程序 net10 Release 编译 | 通过 |
| FullGpu 测试项目（含新增 WinForms 测试）编译 | 通过 |
| 原有 Unit Tests | 498/498 通过，无跳过；结果见第 12 节 |
| 新增 7 项 WinForms 硬件测试执行 | 未执行，交由测试 agent |
| 真实 WinForms 窗口截图、鼠标/键盘、DPI、压力/内存验收 | 未执行，交由测试 agent |
| 全解决方案构建 | 未通过环境准备：workload restore 无法访问 Service Control Manager；restore 报 NETSDK1147，缺 android/maui-tizen |
| NuGet 打包及独立消费验证 | 未执行，见第 10 节 |

历史 WPF、WinUI、UWP、Avalonia 的测试数字不代表 WinForms 已验收。编译通过也不代表窗口能正确显示。

## 2. 主要文件

| 文件（相对仓库根目录） | 用途 |
| --- | --- |
| `src/ScottPlot5/ScottPlot5 Controls/ScottPlot.D3D12.FullGpu/WinForms/ScottPlot.D3D12.WinForms.csproj` | 新控件包 |
| 同目录 `FormsPlotD3D12.cs` | 绘图、输入、线程、句柄和资源生命周期 |
| 同目录 `FormsD3D12PlotMenu.cs` | GPU 栅格导出、剪贴板、GPU 新窗口；SVG 保留矢量导出 |
| `src/ScottPlot5/ScottPlot5 Controls/ScottPlot.D3D12.FullGpu/Core/D3D12HwndPresenter.cs` | DXGI HWND 交换链和 GPU copy/present |
| `src/ScottPlot5/ScottPlot5 Controls/ScottPlot.D3D12.FullGpu/Tests/WinFormsControlTests.cs` | 7 项 NUnit STA 硬件测试 |
| `dev/D3D12GpuValidation/WinForms` | 生产控件验证程序 |
| `dev/D3D12GpuValidation/Shared/SceneCatalog.cs` | Cookbook + 样式 + 补充类型 + 性能场景目录 |
| `dev/D3D12GpuValidation/Shared/WindowSuite.cs` | 逐场景 GPU 截图与外部窗口截图确认协议 |

不要混用旧的 `ScottPlot5 Controls/ScottPlot.D3D12` 曲线回读实现。本次使用 `ScottPlot.D3D12.FullGpu` 下的核心。

## 3. 环境、约定和证据目录

要求 Windows 10 1809 或更新版本、硬件 D3D12、BGRA MSAA4、交互式桌面会话。
第一轮以 Windows x64 为准，记录实际显卡、驱动、Windows 版本、DPI、桌面/远程会话和进程架构。
缺 GPU 或没有交互桌面时写 BLOCKED，不要用 WARP 或 CPU 回退把用例标成 PASS。
ARM64/x86 未在本次验收范围内；若要声称支持，应另开架构矩阵验证原生依赖。

先记录 `git status --short`，保护已有改动。不要 reset、覆盖他人文件、修改 DisplayApp、主动 commit 或发布远程 NuGet。
先读仓库适用的 AGENTS.md 和技能。**本交接明确要求测试 agent 执行下面指定的 FullGpu/WinForms 硬件测试**，除此之外按仓库约定限制测试范围。

在 PowerShell 中执行，后续命令共享以下变量和工作目录：

```powershell
$repo = 'D:\code\classLib\ScottPlot\ScottPlot'
$gpu = Join-Path $repo 'src\ScottPlot5\ScottPlot5 Controls\ScottPlot.D3D12.FullGpu'
$evidence = Join-Path $repo ('artifacts\winforms-d3d12\acceptance-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $evidence -Force | Out-Null
Set-Location (Join-Path $repo 'dev\D3D12GpuValidation')
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
git status --short | Set-Content (Join-Path $evidence 'git-status-before.txt')
git rev-parse HEAD | Set-Content (Join-Path $evidence 'head.txt')
dotnet --info | Set-Content (Join-Path $evidence 'dotnet-info.txt')
Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'os.json')
Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, AdapterRAM |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'adapters.json')
```

此目录的 `global.json` 选择 .NET 10（当前为 10.0.403，允许同系列 feature roll forward）。
不要误用仓库根目录默认 .NET 11 预览 SDK 来解释 .NET 10 的运行结果。机器缺 SDK 时明确记录。
为 net8 运行验证程序时还需要 .NET 8 Desktop Runtime，不能只编译 net8 就声称已运行。

## 4. 构建和自动测试

每条命令后检查 `$LASTEXITCODE`，非零立即保存日志并排查，不要继续累计“通过”结果。

```powershell
dotnet build "$gpu\WinForms\ScottPlot.D3D12.WinForms.csproj" -c Release -m:1 -nr:false -p:NuGetAudit=false *> (Join-Path $evidence 'build-winforms.log')
dotnet build '.\WinForms\D3D12GpuWinFormsValidation.csproj' -c Release -m:1 -nr:false -p:NuGetAudit=false *> (Join-Path $evidence 'build-host.log')
dotnet test "$gpu\Tests\ScottPlot.D3D12.FullGpu.Tests.csproj" -c Release -m:1 -nr:false -p:NuGetAudit=false `
    --filter 'FullyQualifiedName~WinFormsControlTests' `
    --results-directory "$evidence\winforms-tests" --logger 'trx;LogFileName=winforms.trx' *> (Join-Path $evidence 'winforms-tests.log')
```

预期 WinForms 筛选得到 **7 项**测试，全部通过，无忽略、无崩溃：

| 方法名（均以 `Test_WinForms_` 开头） | 核心断言 |
| --- | --- |
| `FullFigurePresentsWithoutReadback` | Direct3D、真实 Present 计数增长、连续绘制读回为 0、显式捕获一次后恰好为 1、红色背景像素 |
| `ResizeZeroSizeAndHandleRecreation` | resize 更新实际宽度、buffer resize +1、零尺寸不 Present、RecreateHandle 后可重绘 |
| `WorkerRefreshAndThreadAffinity` | 1000 次后台 Refresh 被 UI 消费；后台直接访问 GRContext 明确失败 |
| `MultiplotResetAndMenu` | 第二子图布局有效，Reset 恢复单图和 PlotControl，菜单 Reset 仍为 GPU 命令 |
| `SharedDeviceSurvivesOtherControlDisposal` | 同一 UI 线程共享 context，销毁一个控件不影响另一个，重复 Dispose/Refresh 可用 |
| `CaptureRequiresCompletedFrame` | 无完整帧不能截图；已释放控件截图抛 ObjectDisposedException |
| `InputEventsZoomPanAndReleaseCapture` | 控件事件转发改变缩放/平移，鼠标捕获释放，失焦清理修饰键 |

这些测试部分通过受保护方法驱动 Paint/Input；**不能代替真实 WM_PAINT、鼠标输入和窗口截图验收**。
不增加 Assert.Ignore 来隐藏 GPU 初始化问题。测试失败时保存完整异常及精确方法名。

然后执行共享核心和上游回归：

```powershell
dotnet test "$gpu\Tests\ScottPlot.D3D12.FullGpu.Tests.csproj" -c Release -m:1 -nr:false -p:NuGetAudit=false `
    --results-directory "$evidence\fullgpu-tests" --logger 'trx;LogFileName=fullgpu.trx' *> (Join-Path $evidence 'fullgpu-tests.log')
dotnet test "$repo\src\ScottPlot5\ScottPlot5 Tests\Unit Tests\ScottPlot Unit Tests.csproj" -c Release -m:1 -nr:false -p:NuGetAudit=false `
    --results-directory "$evidence\unit-tests" --logger 'trx;LogFileName=unit.trx' *> (Join-Path $evidence 'unit-tests.log')
```

全解决方案验证按 build-full 技能执行 `dotnet workload restore`、`dotnet restore`、`dotnet build --configuration Release`，保存各自日志。
workload 权限/平台 SDK 阻塞时单独记 BLOCKED，不能把定向项目构建写成全解决方案通过。

## 5. 真实窗口首轮验收

交互测试可前台启动。使用后台辅助进程时按环境要求加 `-WindowStyle Hidden`；不要靠工具隐藏窗口来伪造可视验收。
推荐直接运行已构建程序：

```powershell
dotnet '.\WinForms\bin\Release\net10.0-windows\D3D12GpuWinFormsValidation.dll' "$evidence\manual"
```

该命令阻塞到窗口关闭。需要同时操作 UI 时使用工具的异步进程会话或另一个终端。
读运行中的真实 UI 时可按 lvt 技能附加目标进程；截图必须来自生产窗口的实际客户区。
`manual-gpu.png` 是 GPU 纹理捕获，**不能代替窗口截图**。真实截图至少保存首帧、缩放后、resize 后、句柄重建后四张。

| ID | 操作 | 通过标准/证据 |
| --- | --- | --- |
| WF-01 | 启动，等待 2 秒，记录 `diagnostics.json`，截真实窗口 | Figure 完整可见，无黑屏/空白/叠加错位；Backend=Direct3D；PresentedFrames>0；Frame 尺寸等于 Width/Height |
| WF-02 | 保持默认性能场景，点击 Animate，观察 60 秒 | PresentedFrames 持续增长；不点 Capture/不导出期间 PixelReadbackCount 始终为 0；不是只重复显示缓存图片 |
| WF-03 | 点 Capture 两次，间隔一次 Animate 或 Refresh | 同一 HandleGeneration 内读回恰好 +2；PNG 与最新 Figure、尺寸一致；后续普通绘制不继续加读回 |
| WF-04 | 持续拖窗口边框 30 秒；最小化/还原 10 次 | 无异常、无旧尺寸裁剪、无拉伸残影，标题/轴/图例不消失；尺寸变化触发 resize，同尺寸刷新不持续触发 resize |
| WF-05 | 点 Recreate handle 20 次，每次观察首帧 | 图像能恢复；HandleGeneration 增长；每代计数重新开始，不能用跨代读回差值判泄漏 |
| WF-06 | 点 Worker refresh 10 次；立即 resize/关闭各重复一轮 | UI 正常响应；不出现跨线程 GPU 调用或 BeginInvoke 到已销毁窗口的未处理异常；正常关闭有 closed.txt |
| WF-07 | 点 Multiplot，再点 Reset，循环 20 次 | 子图分区、轴、背景和交互正确；Reset 后单图可画，无已释放资源错误 |
| WF-08 | 隐藏/显示、TabPage 切换、Panel 移除再添加控件 | 句柄保留时能恢复；句柄重建时重新初始化；永久移除显式 Dispose，无资源仍被控件持有 |

WF-08 需测试 agent 建立一个最小宿主，继承控件即可；现有验证程序按钮不覆盖父容器切换。
关闭时缺少 closed.txt、出现 fatal.txt、程序挂住均不能判通过。

`diagnostics.json` 每秒更新，包含真实后端、Present/Readback/Resize 计数、HandleGeneration、DPI、物理尺寸、LastGpuFrame。
读取 JSON 时可能正好碰到写入，遇截断先重读，不能只看旧内容；必要时在宿主暂停后复制证据。

## 6. 真实输入、DPI 和菜单

| ID | 操作 | 通过标准 |
| --- | --- | --- |
| IN-01 | 用实际滚轮在 DataRect 中心和边缘各放大/缩小 5 次 | 轴范围变化，指针锚点正确；图、网格和标签同步更新；连续曲线没有断裂 |
| IN-02 | 左拖平移，拖到控件外再松开，返回控件 | 平移方向正确；返回后不继续拖，Capture 已释放 |
| IN-03 | 右拖缩放、中键框选、中键单击自动范围 | 与普通 FormsPlot 相同；右拖结束不误弹菜单 |
| IN-04 | Shift/Ctrl+滚轮，方向键、A 键；Tab 切走/Alt+Tab 切走 | 默认横纵轴约束、键盘平移/缩放和自动范围有效；失焦后无卡住的修饰键或拖拽 |
| IN-05 | 禁用/重新启用 UserInputProcessor；注册一项自定义响应 | 禁用时不变轴，恢复后可操作；没有重复转发同一输入 |
| DPI-01 | 100%、125%、150%、200%，至少两种；有条件跨屏移动 | 物理 ClientSize=LastGpuFrame 尺寸；DisplayScale=DPI/96；无二次缩放、坐标偏移或裁剪 |
| DPI-02 | 每种 DPI 下测试 Plot.ScaleFactor=1、1.5、2 和 Multiplot | 用户 ScaleFactor 保持原值；子图 hit test 与绘图区域一致；不把 DPI 和 ScaleFactor 重复相乘 |
| MENU-01 | 真正右击，测试 Autoscale、自定义条目、separator、Clear/Reset | 命令只执行一次；IPlotMenu.Reset 后仍为 GPU 导出/窗口处理器 |
| MENU-02 | 分别 Save PNG/JPEG/BMP/WebP/SVG，取消一次对话框 | 文件可解码，尺寸/样式正确；SVG 为矢量路径；取消不报错。栅格由 D3D12ImageExport 生成 |
| MENU-03 | Copy to Clipboard，粘贴到画图/其他图像查看器 | 图片完整，颜色/alpha 正常，剪贴板离开方法后仍有效 |
| MENU-04 | Open in New Window，缩放并关闭；重复 20 次 | 子窗口中确为 FormsPlotD3D12，无普通 CPU FormsPlot；关闭后原 PlotControl 归属恢复，主控件可继续交互 |
| MENU-05 | Multiplot 的第二子图内打开菜单并导出 | 命中正确子图；菜单栅格导出为选中子图；CaptureBitmap 为整个控件视口 |

Wheel action 按钮只调用标准 UserInputProcessor，并写 wheel-action.json，不证明真实滚轮事件接线正确。
菜单导出使用临时 D3D12Renderer，因此其读回不计入主控件 PixelReadbackCount；判断“无逐帧读回”时先关闭导出操作。
显式栅格导出允许一次 GPU→CPU 读回；SVG 保留既有矢量导出。绘图类型内部既有 Hittable/Draggable CPU 命中图不等于呈现回退。

## 7. 全绘图类型/样式场景套件

先运行小子集验证截图协议：

```powershell
dotnet '.\WinForms\bin\Release\net10.0-windows\D3D12GpuWinFormsValidation.dll' "$evidence\catalog-smoke" `
    --suite '--scenes=Styles/,Extra/'
```

去掉 `--scenes` 运行完整目录。场景数由当前 SceneCatalog/程序集动态生成，不能照抄旧报告的 403/67。
`--scenes` 为名字的“不区分大小写的包含”筛选，逗号分隔为 OR。
断点续测使用同样的 `--scenes=...` 加 `--skip=N`；现有 Select 只有提供 --scenes 时才处理 skip。
重新运行必须使用新目录，避免旧 capture-ack.txt 或 suite-completed.txt 干扰。

每个场景的协议：

1. 读取证据目录的 `scene-ready.json`，确认当前 `Index`、`Name`、`Error`；错误非空时记录 FAIL。
2. 用 lvt/可用桌面截图工具获取该窗口真实图像，保存为 JSON `Output` 字段指定的 `NNNN-lvt.png`。
3. 打开真实截图，检查轴、文字、背景和内容；对照同目录 `NNNN-gpu.png`，需要时另建普通 FormsPlot 同场景作基线。
4. 只有截图已经保存并审阅后才写确认：

```powershell
$dir = Join-Path $evidence 'catalog-smoke'
$ready = Get-Content (Join-Path $dir 'scene-ready.json') -Raw | ConvertFrom-Json
# 在这里先实际保存并检查 $ready.Output 指定的窗口截图。
[string]$ready.Index | Set-Content (Join-Path $dir 'capture-ack.txt')
```

5. 等待 Index 变化，重复。最后必须有 suite-completed.txt、window-suite.json.Completed=true、Cases 数等于 Expected。
6. 检查 Inventory 每个具体类型都有 Covered=true；缺少覆盖时新增最小场景或如实写缺口。
7. 每个 Case 的 `REAL_WINDOW_GPU_RENDER_PASS` 只表示宿主渲染/捕获没有抛异常；必须结合真实截图和人工判定。

注意：共享 WindowSuite 的 `PerRenderReadbacks`、`DiagnosticReadbacks` 和 `NativeMsaaSamples` 为历史协议的声明值，**不是 WinForms 的计数测量结果**。
用 WinForms `diagnostics.json` 和自动测试验证计数；目录每个成功场景有一次显式捕获，故 suite 模式下主控件读回不应为 0。
同一代 renderer、两次 suite 诊断捕获之间，普通重绘应无额外读回。严禁只凭 window-suite.json 的静态字段宣称通过。

至少覆盖：所有具体 Plottable、全部 marker/hatch、线帽/连接/虚线、透明/渐变/图片、字体和中文/RTL、裁剪和 anti-alias、
Signal/SignalXY 原位修改与 NaN/Inf/反轴/旋转、多图/共享轴、图例/标题/网格/背景；Benchmark 动态计时文字比较时允许预先说明的动态区域差异。
CPU 与 GPU 抗锯齿/字体允许经解释的像素差异，不要求无条件 byte-for-byte 相等；漏元素、错位和几何断裂均判失败。

## 8. 资源、设备异常与长时间压力

使用 Animate 持续完整重绘；16ms Timer 是请求频率，不承诺 60 FPS。每秒 JSON/状态标签会产生分配，应与渲染器分配分开记录。

| ID | 步骤 | 判定 |
| --- | --- | --- |
| LIFE-01 | 同线程同时放 4 个 GPU 控件，逐个关闭；再在第二 STA UI 线程开一个窗口 | 同线程 context 共享，另一线程独立；关闭一个不损坏其他，最终所有 leases 释放 |
| LIFE-02 | create/show/render/dispose 连续 100 次；记录每 10 次内存、句柄、GDI/USER 对象 | 预热后无随次数线性增长的未释放资源；重复 Dispose 安全；Dispose 后截图明确失败 |
| LIFE-03 | 800×600↔1920×1080 resize 200 次，回到初始尺寸 | swapchain 可持续重建；核心 capacity 允许保留峰值容量，第二轮同一尺寸序列不继续增加 target allocations |
| STRESS-01 | Animate 30 分钟，每 5 分钟 resize/最小化/恢复，关闭导出 | 无崩溃/黑屏；同代读回恒定；记录起止和每分钟样本，不仅是最后一张图 |
| FAIL-01 | 无可用硬件/MSAA4 或不支持系统的独立测试环境 | 明确 GPU 初始化异常，无 WARP、Raster SKSurface 或普通 FormsPlot 回退 |
| FAIL-02 | 可控设备丢失/锁屏恢复/远程会话切换，按实际权限测试 | 设备失效时报告异常，无 CPU 回退；恢复后由宿主重建控件再绘制。未注入成功就写未验证 |
| THREAD-01 | 后台 Refresh 与 UI 句柄销毁/重建并发；后台 GRContext/Capture/Dispose | Refresh 可安全排队/忽略已销毁控件；直接 GPU 操作明确线程异常，无悬挂资源 |

内存采样至少记录 PrivateBytes、WorkingSet、托管堆/分配、进程句柄、GDI/USER 数、专用/共享 GPU 内存。
强制 GC 只用于“循环结束后是否可回收”的单独阶段，不能放在每帧或性能采样中。
GPU/驱动缓存、核心保留峰值纹理需要与线性泄漏区分；提供时间序列和第二轮比较，不设脱离硬件的绝对 MB 阈值。
WinForms 本次没有 WPF 历史文档中的“跳过 Intel 内存验收”例外。

## 9. 性能测量口径

使用 Release、同 GPU/电源模式/DPI，记录实际视口、图类型和数据量。默认性能目录可选32条×20000点 SignalXY及完整图表装饰。
读取实际 SceneCatalog 确认数据规模；不要把场景名字直接当成实测配置。
至少预热 30 帧、测量 300 帧，记录 p50/p95/p99、吞吐、UI线程分配、设备/驱动和 GPU memory。
`LastGpuFrame.TotalMilliseconds` 包含 Render、Skia submit、GPU resolve 和实际 fence，**排除** HWND GPU copy、Present/vsync、WinForms 调度及桌面合成。
整帧用户可见延迟另行用 Stopwatch 包围实际 Paint/Present 并结合呈现工具测量；不能把核心数值当最终 FPS。
对 SynchronizeToDisplay=true/false 分别测量；关闭 vsync 不等于开启 DXGI allow-tearing，不能混淆。
测量期间禁止 CaptureBitmap/Save/Clipboard/目录截图，记录 readback 前后值一致；主动改变数据或显式重绘证明完整 Figure 在执行。
普通 CPU FormsPlot 对比需相同数据、样式、分辨率和采样口径。不要复用历史曲线回读/WPF 性能数字。

## 10. NuGet/独立消费验证（验收后执行，不发布）

先完成硬件和窗口验收，再用新的唯一版本、干净 artifacts 输出打包。
现有 Build-LocalPackages.ps1/Publish-NuGetPackages.ps1 可打包整个多平台家族，已经加入 WinForms；其默认版本是旧值，必须显式传新版本。
Publish-NuGetPackages.ps1 默认包含推送行为，本次不要运行其发布路径。

WinForms 最小闭包为 ScottPlot、ScottPlot.WinForms、ScottPlot.D3D12.Core、ScottPlot.D3D12.WinForms。
在证据目录下依次生成这四个包，统一指定 `Version`/`PackageVersion`，保留每个 pack 日志和 SHA256：

```powershell
$packageVersion = '5.1.61-gpu.2' # 若已分发过相同版本，选择更高的唯一版本
$packageDir = Join-Path $evidence 'packages'
$projects = @(
    "$repo\src\ScottPlot5\ScottPlot5\ScottPlot.csproj",
    "$repo\src\ScottPlot5\ScottPlot5 Controls\ScottPlot.WinForms\ScottPlot.WinForms.csproj",
    "$gpu\Core\ScottPlot.D3D12.Core.csproj",
    "$gpu\WinForms\ScottPlot.D3D12.WinForms.csproj"
)
foreach ($project in $projects) {
    $id = [IO.Path]::GetFileNameWithoutExtension($project)
    dotnet pack $project -c Release -m:1 -nr:false -p:NuGetAudit=false `
        "-p:Version=$packageVersion" "-p:PackageVersion=$packageVersion" `
        -p:UseArtifactsOutput=true "-p:ArtifactsPath=$evidence\clean-$id" `
        --output $packageDir *> (Join-Path $evidence "pack-$id.log")
    if ($LASTEXITCODE -ne 0) { throw "Pack failed: $id" }
}
Get-ChildItem $packageDir -Filter '*.nupkg' | Get-FileHash -Algorithm SHA256 |
    ConvertTo-Json | Set-Content (Join-Path $evidence 'package-hashes.json')
```

解压检查 nuspec 的 package id、Core 版本下限、普通 WinForms/Skia/Vortice 依赖；不能引用缺少 D3D12HwndPresenter 的旧 Core。
新建只依赖新 WinForms NuGet 包的独立 net8/net10 WinForms 消费程序，不使用 ProjectReference，确保本地 feed 的四个新包被真正解析。
重复首帧、缩放、菜单和关闭验收，记录实际加载程序集路径/版本和 nupkg hash；不能以本仓库已有 bin 目录代替消费测试。
如使用 ForkIdPrefix，还要检查所有 ScottPlot 家族依赖都使用同一前缀。

## 11. 当前行为边界

- GPU 视口不透明。Plot 内部透明度正常参与 GPU 绘制；不支持借此透出 WinForms 父控件背景。
- DrawToBitmap/GDI/打印捕获不属于 GPU 截图 API，使用 CaptureBitmap 显式读回。
- 设计器不创建 D3D12 设备；本次不承诺设计器内实时预览 Figure。
- 设备丢失没有自动恢复/CPU 回退。宿主接到异常后处理环境并重建 GPU 控件。
- Refresh 只调度 UI 重绘，不锁定调用方数据。后台更新数组必须与 `Plot.Sync` 协调。
- 计数器为当前 renderer/句柄生命周期内的值，重建后归零；设备共享不代表计数共享。
- 直接 `Plot.SavePng` 等原 ScottPlot API 保持原行为；默认 GPU 菜单或 D3D12ImageExport 才选择 GPU 栅格导出路径。

## 12. 测试报告要求与交接 prompt

实现轮 Unit Tests：**498/498 通过，0 失败，0 跳过**。沙箱内首轮因 testhost 本地通信超时中止；沙箱外重跑通过（执行约 9 秒）。
成功 TRX：`artifacts/winforms-d3d12/implementation/unit-unsandboxed/unit.trx`。
编译日志：`artifacts/winforms-d3d12/implementation/` 下的 `ScottPlot.D3D12.WinForms-build.log`、`D3D12GpuWinFormsValidation-build.log`、`ScottPlot.D3D12.FullGpu.Tests-build.log`。
这批结果不包括新增 WinForms 硬件测试的执行，也不包括窗口/DPI/压力验收。

报告保存至证据目录的 `REPORT.md`，每个用例列：ID、PASS/FAIL/BLOCKED/NOT_RUN、环境、步骤、期望、实际、证据路径、缺陷/重现命令。
另列构建/自动测试数量及 TRX、真实窗口截图、计数前后值、全目录覆盖、压力时间序列、性能口径、剩余缺口。
有 P0/P1 缺陷或首帧/输入/生命周期/无逐帧读回未通过时，不下“WinForms D3D12 已验收”结论。
机器缺条件时汇报准确阻塞项；未经实际执行的项目一律 NOT_RUN，不能把本文预期结果写成实测。

可复制给测试 agent：

```text
请在 D:\code\classLib\ScottPlot\ScottPlot 实际测试本轮新增 WinForms D3D12 后端。
先读 WINFORMS-D3D12-TEST-HANDOFF.md、适用 AGENTS.md 和技能，记录 git status，保留已有改动。
使用 ScottPlot.D3D12.FullGpu 下的新 WinForms 项目，不要测试旧曲线回读包来代替。

本任务明确授权并要求执行文档指定的 WinFormsControlTests 和 FullGpu 硬件测试，以及普通 Unit Tests 回归。
请依文档完成双目标编译、7 项 WinForms 测试、真实窗口/真实输入、DPI、菜单导出和新窗口、句柄重建、
多控件/线程、目录场景截图、30 分钟压力和资源验收；条件不足项明确 BLOCKED/NOT_RUN。
先用小场景子集验证截图/ack 协议，再跑完整目录。每个 ack 之前先真实保存和检查窗口截图。
重点证明没有逐帧 GPU→CPU 读回或 CPU/WARP 回退，使用实际计数器，不能信共享套件的静态声明字段。
不要把 GPU 纹理截图当作窗口呈现证据，也不要把 LastGpuFrame 核心耗时当成含 Present 的 FPS。

结果、TRX、截图、计数器和压力样本放到新的 artifacts/winforms-d3d12/acceptance-* 目录，写 REPORT.md。
发现缺陷先给出精确重现和影响；可以做完成验证必需的最小修复并重跑受影响项，说明改动。
完成验收后才从干净 staging 做本地 NuGet 和独立消费测试，不发布远程包，不主动 commit，不改 DisplayApp。
最终中文报告列已通过、失败、阻塞、未测试项和证据绝对路径，不复用历史测试数字。
```
