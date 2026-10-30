using System.Reflection;
using ScottPlot;
using ScottPlot.DataSources;
using ScottPlot.Plottables;
using ScottPlotCookbook;
using SkiaSharp;

/// <summary>Creates repeatable existing recipes plus explicit missing-type and style capability scenes.</summary>
internal sealed record SceneDefinition(string Name, Action<Plot> Build, string Capability, bool AuxiliaryRasterHitMap = false)
{
    internal Plot Create()
    {
        Generate.RandomData.Seed(0); Fonts.Reset(); LabelStyle.RTLSupport = false;
        Plot plot = new();
        try { Build(plot); return plot; } catch { plot.Dispose(); throw; }
    }
}

/// <summary>Inventories every concrete core plottable and a reproducible visual acceptance catalogue.</summary>
internal static class SceneCatalog
{
    internal static IReadOnlyList<SceneDefinition> All { get; } = Create();
    internal static string CanonicalType(Type type) => (type.IsGenericType ? type.GetGenericTypeDefinition() : type).FullName!;

    /// <summary>Full catalogue, or the subset whose names contain any --scenes=substringA,substringB filter; --skip=N drops the first N matches for resuming.</summary>
    internal static IReadOnlyList<SceneDefinition> Select(string[] args)
    {
        string? spec = args.FirstOrDefault(a => a.StartsWith("--scenes=", StringComparison.OrdinalIgnoreCase));
        if (spec is null) return All;
        string[] substrings = spec.Substring("--scenes=".Length).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        List<SceneDefinition> matches = All.Where(scene => substrings.Any(t => scene.Name.Contains(t, StringComparison.OrdinalIgnoreCase))).ToList();
        if (matches.Count == 0) throw new InvalidOperationException("No scenes matched --scenes filter: " + spec);
        string? skipSpec = args.FirstOrDefault(a => a.StartsWith("--skip=", StringComparison.OrdinalIgnoreCase));
        if (skipSpec is not null && int.TryParse(skipSpec.Substring("--skip=".Length), out int skip)) matches = matches.Skip(skip).ToList();
        return matches;
    }

    private static List<SceneDefinition> Create()
    {
        List<SceneDefinition> scenes = [];
        foreach (Type type in typeof(IRecipe).Assembly.GetTypes().Where(t => typeof(IRecipe).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null).OrderBy(t => t.FullName))
        {
            Type captured = type;
            scenes.Add(new(type.FullName!, p => ((IRecipe)Activator.CreateInstance(captured)!).Execute(p), "Cookbook/" + type.DeclaringType?.Name));
        }
        scenes.Add(new("Extra/Benchmark", p => { Basic(p); p.PlottableList.Add(new Benchmark { IsVisible = true }); }, "Benchmark"));
        scenes.Add(new("Extra/DataStreamerXY", p =>
        {
            DataStreamerXY stream = new(128) { ManageAxisLimits = false, LineWidth = 2 };
            for (int i = 0; i < 128; i++) stream.Add(i * .1, Math.Sin(i * .1));
            p.PlottableList.Add(stream); p.Axes.SetLimits(-1, 14, -1.5, 1.5); p.Title("DataStreamerXY");
        }, "DataStreamerXY"));
        scenes.Add(new("Extra/HittablePlottableDecorator", p =>
        {
            var scatter = p.Add.Scatter(Generate.Consecutive(100, .1), Generate.Sin(100));
            p.PlottableList.Remove(scatter); p.PlottableList.Add(new HittablePlottableDecorator(scatter)); p.Axes.AutoScale(); p.Title("Hittable decorator / GPU Figure");
        }, "HittablePlottableDecorator", true));
        scenes.Add(new("Extra/DraggablePlottableDecorator", p =>
        {
            var scatter = p.Add.Scatter(Generate.Consecutive(100, .1), Generate.Cos(100));
            p.PlottableList.Remove(scatter); DraggablePlottableDecorator drag = new(scatter); drag.StartDrag(new(0, 0)); drag.DragTo(new(.4, .2));
            p.PlottableList.Add(drag); p.Axes.AutoScale(); p.Title("Draggable decorator / GPU Figure");
        }, "DraggablePlottableDecorator", true));
        scenes.Add(new("Extra/IsoLines", p =>
        {
            Basic(p); IsoLines lines = new(); lines.ManualPositions.Add((0, "zero")); lines.ManualPositions.Add((.5, "half")); p.PlottableList.Add(lines); p.Title("IsoLines");
        }, "IsoLines"));
        scenes.Add(new("Extra/DeprecatedSignalConstDouble", p =>
        {
            Type generic = typeof(Plot).Assembly.GetType("ScottPlot.Plottables.SignalConst" + (char)96 + "1")!.MakeGenericType(typeof(double));
            SignalConstSource<double> source = new(Generate.Sin(1000), .01);
            p.PlottableList.Add((IPlottable)Activator.CreateInstance(generic, source)!); p.Axes.AutoScale(); p.Title("Deprecated generic SignalConst<double>");
        }, "SignalConst<T>"));
        scenes.Add(new("Extra/TINPlot", p =>
        {
            List<Coordinates3d> coordinates = [];
            Random random = new(1);
            for (int x = 0; x < 5; x++) for (int y = 0; y < 4; y++)
            {
                double a = x + random.NextDouble() * .2, b = y + random.NextDouble() * .2;
                coordinates.Add(new(a, b, Math.Sin(a) * Math.Cos(b)));
            }
            p.PlottableList.Add(new TINPlot(coordinates) { LegendText = "TIN / Voronoi / contours" }); p.Axes.AutoScale(); p.ShowLegend(); p.Title("Triangulated irregular network");
        }, "TINPlot"));

        scenes.Add(new("Styles/AllMarkerShapes", Markers, "Every MarkerShape enum value"));
        scenes.Add(new("Styles/AllParameterlessHatches", Hatches, "Every built-in parameterless IHatch"));
        foreach (LinePattern pattern in LinePattern.GetAllPatterns().Append(new([9, 3, 2, 3], 2, "CustomPhase")))
        {
            LinePattern captured = pattern;
            scenes.Add(new("Styles/LinePattern-" + pattern.Name, p => { Basic(p); foreach (var signal in p.PlottableList.OfType<SignalXY>()) signal.LinePattern = captured; p.Title("Pattern / " + captured.Name); }, "LinePattern/" + pattern.Name));
        }
        foreach (SKStrokeCap cap in Enum.GetValues<SKStrokeCap>())
        foreach (SKStrokeJoin join in Enum.GetValues<SKStrokeJoin>())
        foreach (bool aa in new[] { false, true })
        {
            SKStrokeCap c = cap; SKStrokeJoin j = join; bool a = aa;
            scenes.Add(new($"Styles/Stroke-{cap}-{join}-AA{aa}", p =>
            {
                for (int i = 0; i < 4; i++)
                {
                    var line = p.Add.Scatter(new double[] { 0, 1, 2, 3, 4 }, new double[] { i, i + .7, i, i + .7, i });
                    line.LineWidth = i == 0 ? 0 : i * 3; line.LineStyle.Hairline = i == 0; line.LineStyle.AntiAlias = a; line.LineStyle.StrokeCap = c; line.LineStyle.StrokeJoin = j;
                    line.Color = new ScottPlot.Palettes.Category10().GetColor(i).WithAlpha(.7); line.MarkerSize = 0;
                }
                p.Axes.SetLimits(-.5, 4.5, -.5, 4.5); p.Title($"Stroke caps/joins / AA={a}");
            }, "StrokeCap/Join/Hairline/AA/Alpha"));
        }
        scenes.Add(new("Styles/UnicodeRotatedLabels", p =>
        {
            Basic(p);
            for (int i = 0; i < 4; i++)
            {
                Text text = p.Add.Text("GPU / 文字 / αβγ\nSecond line", 2 + i * 2, -.9 + i * .55);
                text.LabelStyle.FontName = "Microsoft YaHei UI"; text.LabelStyle.FontSize = 12 + i * 3; text.LabelStyle.Rotation = -15 + i * 15;
                text.LabelStyle.Bold = i % 2 == 0; text.LabelStyle.Italic = i % 2 != 0; text.LabelStyle.Underline = true;
                text.LabelStyle.BackgroundColor = Colors.Yellow.WithAlpha(.3); text.LabelStyle.BorderColor = Colors.Gray; text.LabelStyle.BorderWidth = 1;
                text.LabelStyle.ShadowColor = Colors.Gray.WithAlpha(.4); text.LabelStyle.PixelPadding = new(4);
            }
            p.Title("Unicode / styled and rotated labels");
        }, "Font/Unicode/rotation/bold/italic/underline/background/border/shadow"));
        scenes.Add(new("Performance/FullFigure32x20000AA", p =>
        {
            double[] xs = Enumerable.Range(0, 20000).Select(i => i * .01).ToArray();
            for (int i = 0; i < 32; i++)
            {
                int curve = i; var s = p.Add.SignalXY(xs, xs.Select(x => Math.Sin(x * (1 + curve % 5) + curve * .1) + Math.Sin(x * 50 + curve) * .2).ToArray());
                s.LineWidth = 2; s.LineStyle.AntiAlias = true; if (i < 2) s.LegendText = $"SignalXY {i + 1} / 20,000";
            }
            p.Add.Bars(new Bar[] { new() { Position = 30, Value = .6, Size = 6 }, new() { Position = 90, Value = .8, Size = 6 }, new() { Position = 150, Value = .4, Size = 6 } }).LegendText = "Bars";
            p.Add.Text("Full Figure D3D12", 110, -1.3).LabelFontSize = 16;
            p.Title("D3D12 full Figure / 32 x 20,000 / AA"); p.XLabel("Time (s)"); p.YLabel("Amplitude"); p.ShowLegend(); p.Axes.SetLimits(-10, 210, -1.6, 1.6);
        }, "Complete dense AA workload"));
        scenes.Add(new("Styles/TransparentFigure", p => { Basic(p); p.FigureBackground.Color = Colors.Transparent; p.DataBackground.Color = Colors.Transparent; p.Title("Transparent Figure"); }, "Premultiplied alpha"));
        scenes.Add(new("Scale/SignalXY-16x500000", p =>
        {
            double[] xs = Enumerable.Range(0, 500000).Select(i => i * .002).ToArray();
            for (int i = 0; i < 16; i++)
            {
                int curve = i;
                var s = p.Add.SignalXY(xs, xs.Select(x => Math.Sin(x * (1 + curve % 4) + curve * .13) * Math.Exp(-Math.Pow(x - 400 - 60 * curve, 2) / 40000) + Math.Sin(x * 37 + curve * 2) * .15).ToArray());
                s.LineWidth = 1.5f; s.LineStyle.AntiAlias = true;
            }
            p.Title("D3D12 scale / 16 x 500,000 SignalXY (8M points)"); p.XLabel("Time (ms)"); p.Axes.SetLimits(-10, 1010, -2.2, 2.2);
        }, "Dense multi-curve large data"));
        scenes.Add(new("Scale/Scatter-2x500000", p =>
        {
            for (int i = 0; i < 2; i++)
            {
                int c = i;
                double[] xs = Enumerable.Range(0, 500000).Select(j => j * .002 + c).ToArray();
                double[] ys = xs.Select((x, j) => Math.Sin(x * 3 + c) + Math.Cos(x * .7 + j * 1e-5) * .3).ToArray();
                Scatter scatter = p.Add.Scatter(xs, ys);
                scatter.LineWidth = 1; scatter.MarkerSize = 0; scatter.Color = new ScottPlot.Palettes.Category10().GetColor(c);
            }
            p.Title("D3D12 scale / 2 x 500,000 Scatter (no markers)"); p.Axes.SetLimits(-1, 1002, -2, 2);
        }, "Large scatter lines"));
        scenes.Add(new("Scale/Signal-1x2000000", p =>
        {
            double[] ys = Enumerable.Range(0, 2000000).Select(i => Math.Sin(i * 1e-3) + Math.Sin(i * 1e-5) * .5).ToArray();
            p.Add.Signal(ys, period: .001).LineWidth = 1;
            p.Title("D3D12 scale / 1 x 2,000,000 Signal"); p.Axes.SetLimits(-100, 2100, -2, 2);
        }, "Single very long signal"));
        scenes.Add(new("Scale/Heatmap-1500x1500", p =>
        {
            double[,] data = new double[1500, 1500];
            for (int y = 0; y < 1500; y++) for (int x = 0; x < 1500; x++) data[y, x] = Math.Sin(x * .01) * Math.Cos(y * .01) + Math.Sin(Math.Sqrt(x * x + y * y) * .02);
            p.Add.Heatmap(data);
            p.Title("D3D12 scale / 1500 x 1500 Heatmap");
        }, "Large heatmap"));
        return scenes;
    }

    private static void Basic(Plot p)
    {
        double[] xs = Generate.Consecutive(1000, .01);
        for (int i = 0; i < 3; i++)
        {
            int c = i; var signal = p.Add.SignalXY(xs, xs.Select(x => Math.Sin(x * (c + 1))).ToArray()); signal.LegendText = "Signal " + i; signal.LineWidth = 2;
        }
        p.Add.Bars(new Bar[] { new() { Position = 2, Value = .5, Size = .4 }, new() { Position = 6, Value = .7, Size = .4 } }).LegendText = "Bars";
        p.Add.Text("Full Figure GPU", 4, -1.15);
        p.XLabel("X axis"); p.YLabel("Y axis"); p.ShowLegend(); p.Axes.SetLimits(-.5, 10.5, -1.5, 1.5);
    }
    private static void Markers(Plot p)
    {
        MarkerShape[] shapes = Enum.GetValues<MarkerShape>();
        for (int i = 0; i < shapes.Length; i++)
        {
            int col = i % 6, row = i / 6; var marker = p.Add.Marker(col * 4, row * 2);
            marker.MarkerStyle.Shape = shapes[i]; marker.MarkerStyle.Size = 16;
            p.Add.Text(shapes[i].ToString(), col * 4 + .4, row * 2).LabelFontSize = 10;
        }
        p.Axes.SetLimits(-1, 24, -1, Math.Ceiling(shapes.Length / 6.0) * 2); p.Title("Every MarkerShape");
    }
    private static void Hatches(Plot p)
    {
        Type[] types = typeof(IHatch).Assembly.GetTypes().Where(t => typeof(IHatch).IsAssignableFrom(t) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) is not null).OrderBy(t => t.FullName).ToArray();
        for (int i = 0; i < types.Length; i++)
        {
            int col = i % 4, row = i / 4;
            var rectangle = p.Add.Rectangle(col * 3, col * 3 + 2.5, row * 2, row * 2 + 1.4);
            rectangle.FillStyle.Color = Colors.LightBlue; rectangle.FillStyle.HatchColor = Colors.DarkBlue; rectangle.FillStyle.Hatch = (IHatch)Activator.CreateInstance(types[i])!;
            p.Add.Text(types[i].Name, col * 3, row * 2 + 1.7).LabelFontSize = 9;
        }
        p.Axes.SetLimits(-.4, 12, -.4, Math.Ceiling(types.Length / 4.0) * 2 + .5); p.Title("Built-in hatch styles");
    }
}


