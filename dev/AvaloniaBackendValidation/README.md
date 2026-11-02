# Avalonia 可选后端验证

该程序只使用正式 `AvaPlot` 控件和 `ScottPlot.Avalonia.D3D12` provider，不继承控件或覆盖其渲染方法。原型探针保留在相邻目录。

```powershell
./Run-Validation.ps1 -Close
```

脚本运行硬件后端、未注册 provider、实际软件合成器三种进程，并通过 lvt 在后台截图；`-Close` 使用 WindowPattern 关闭测试窗口。可用 `-NoBuild` 验证已经构建的程序。

硬件验证覆盖逻辑像素 API 一致性、自定义 Plot.ScaleFactor、12 次 resize、实际显示器 DPI 和缩放锚点、4 次卸载重建、Skia/D3D12 来回切换、初始化与运行时错误、显式重试、Reset、Multiplot、正常帧零读回，以及明确单次 GPU 导出。错误测试必须保留 Direct3D12 选择、原始异常和错误事件，不能自动进入 Skia。

程序默认保留窗口供 lvt 使用 InvokePattern 测试 Skia、D3D12、Inject failure、Retry。真实鼠标前台输入不属于该后台检查流程。

本次结果、包文件位置和全解决方案构建限制见 [RESULTS.md](RESULTS.md)。
