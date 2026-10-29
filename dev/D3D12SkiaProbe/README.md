# D3D12 / Skia GPU 探针

结论和限制见仓库根目录 [D3D12-SKIA-GPU-PROBE-RESULTS.md](../../D3D12-SKIA-GPU-PROBE-RESULTS.md)。

这些是第一阶段的独立验证程序，不是已完成的 ScottPlot.D3D12 控件或新 NuGet。旧 Sandbox/solution 保留原状态，使用以下直接项目构建隔离它。

## 离屏、配方和内存复现

本轮环境为 Windows x64、.NET SDK 10.0.401、硬件 D3D12；SkiaSharp/NativeAssets.Win32 固定 3.119.0、Vortice 固定 3.8.3。

从此目录运行：

~~~powershell
.\Run-OffscreenProbes.ps1
~~~

脚本创建新的 timestamp 输出目录，依次执行：

1. 外部 D3D12 texture + Skia GPU context/surface API 检查。
2. 2×1,000 Scatter 的普通完整 Figure。
3. 32×20,000 SignalXY 的普通完整 Figure。
4. 现有 Cookbook 单 Plot 配方 GPU 提交冒烟和全部核心类型覆盖表。
5. 离屏 resize 默认缓存/32MiB限额清理的诊断对照。

任何 GPU/API/构建条件失败即退出，没有 CPU surface 回退。脚本不启动窗口，不注册应用，不运行旧曲线扩展，不打包。

性能循环没有像素读回，之后的一张 PNG 是显式诊断导出。兼容性冒烟不等于视觉等价。内存候选不是已验证的三平台修复。

## WPF

~~~powershell
dotnet build ../D3D12SkiaWpfProbe/D3D12SkiaWpfProbe.csproj -c Release --artifacts-path <新的绝对输出目录>
~~~

运行生成的 D3D12SkiaWpfProbe.exe，并将证据输出目录作为第一个参数。源码设置 ShowActivated=false；检查和关闭使用 lvt，不发送桌面输入。WPF 验证依赖已有 D3D11On12Interop.cs，但不引用旧扩展项目。

## WinUI 3

使用已安装的 Visual Studio MSBuild。dotnet build 在本机不能正确定位 PRI packaging task；本轮使用：

~~~powershell
& 'C:\Program Files\Microsoft Visual Studio\18\Professional\MSBuild\Current\Bin\amd64\MSBuild.exe' ../D3D12SkiaWinUiProbe/D3D12SkiaWinUiProbe.csproj /restore /p:Configuration=Release /p:ArtifactsPath=<新的绝对输出目录> /p:NuGetAudit=false /v:minimal
~~~

运行生成的 exe，以证据目录为参数。窗口通过 AppWindow.Show(false) 显示。manifest 必须保留 PerMonitorV2，否则系统可能放大低分辨率位图。

## 真实 UWP

~~~powershell
..\D3D12SkiaUwpProbe\Prepare-UwpProbe.ps1
~~~

脚本仅构建自包含 .NET 10 版本、复制 manifest/assets 并返回新 staging 路径，不注册或激活。需要实际验证时，显式开发注册：

~~~powershell
Add-AppxPackage -Register '<staging>\AppxManifest.xml'
~~~

随后打开“ScottPlot D3D12 UWP Probe”。正常 UWP 激活可能切换前台，必须遵守用户当时的许可；不要自动抢焦点。它使用 Windows.UI.Xaml/CoreWindow 和 appContainer，不是 WinUI 3 或 XAML Island。未验证旧 .NET Native 和 NativeAOT。

日志写入当前用户 LocalAppData/Packages/ScottPlot.D3D12.SkiaProbe.20261028_whfqgfhe95z7g/LocalState。复制诊断资料后可清理本轮专用注册：

~~~powershell
Get-AppxPackage -Name ScottPlot.D3D12.SkiaProbe.20261028 | Remove-AppxPackage
~~~

本轮注册已清理。UWP Window.Closed 的 GPU 资源释放未通过验证，正式实现必须处理挂起/恢复，不要仅靠窗口关闭事件。

## lvt：不抢焦点的检查

先按正确 PID/唯一标题定位：

~~~powershell
lvt dump --uia --pid <WPF或WinUI进程ID> --depth 3
lvt screenshot --pid <WPF或WinUI进程ID> --output <绝对PNG路径>
lvt screenshot --title 'ScottPlot D3D12 UWP Probe' --output <绝对PNG路径>
~~~

UWP 的顶层窗口由 ApplicationFrameHost 承载，应用 PID 没有顶层窗口不代表呈现失败。后台交互先 dump 找到“Zoom probe”/“Reset probe”的当前元素编号和 Invoke pattern，再使用 lvt invoke；不要使用可能回退 SendInput 的 click/type/press-key。

## 内存与资源

本轮报告保存真实窗口 Private Bytes、Working Set、部分 .NET counters、GPU counters，以及离屏 QueryVideoMemoryInfo/Skia cache 对照。不要用 GC heap 代替整个进程内存；不要把 Working Set、Private Bytes、共享 GPU 内存直接相加。

正式验收还需固定尺寸循环、peak/settled、重复轮次增长、资源计数上限、UWP suspend/resume、设备丢失、关闭释放、分配与 GC 暂停，并在限额/清理策略下重测性能。没有这些证据前禁止重新打包。
