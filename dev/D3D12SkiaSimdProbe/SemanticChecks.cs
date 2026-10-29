using System.Runtime.InteropServices;
using ScottPlot;
using SkiaSharp;

/// <summary>Compares optimized sampling with the repository reference, including mutable and exceptional data.</summary>
internal static class SemanticChecks
{
    internal static object Run(GpuHarness gpu, GpuTarget target)
    {
        int comparisons = 0, points = 0, paths = 0, expectedExceptions = 0, mutations = 0;
        long vectorRanges = 0, specialRanges = 0; int nanInputs = 0, infinityInputs = 0, signedZeroInputs = 0;
        using Plot plot = new();
        plot.Axes.SetLimits(-10, 210, -2, 2);
        plot.RenderManager.RenderStarting += (_, rp) =>
        {
            if (comparisons != 0) return;
            Axes axes = new() { XAxis = plot.Axes.Bottom, YAxis = plot.Axes.Left, DataRect = rp.DataRect };
            foreach (int length in new[] { 1, 2, 3, 37, 20000 })
            foreach (string data in new[] { "smooth", "spikes", "nan-zero-infinity", "duplicate-x" })
            foreach (bool simd in new[] { false, true })
            {
                double[] xs = Enumerable.Range(0, length).Select(i => data == "duplicate-x" ? Math.Floor(i / 4.0) * .04 : i * .01).ToArray();
                double[] ys = xs.Select((x, i) => Math.Sin(x * 2) + Math.Cos(i * .17) * .1).ToArray();
                if (data == "spikes") for (int i = 3; i < length; i += 97) ys[i] = i % 2 == 0 ? 999 : -999;
                if (data == "nan-zero-infinity")
                    for (int i = 0; i < length; i += 73)
                        ys[i] = ((i / 73) % 5) switch { 0 => double.NaN, 1 => -0.0, 2 => 0.0, 3 => double.PositiveInfinity, _ => double.NegativeInfinity };
                nanInputs += ys.Count(double.IsNaN); infinityInputs += ys.Count(double.IsInfinity); signedZeroInputs += ys.Count(y => BitConverter.DoubleToInt64Bits(y) == long.MinValue);
                FrameClock clock = new();
                FastSignalSource fast = new(xs, ys, clock, null, simd);
                foreach (bool rotated in new[] { false, true })
                foreach (bool primaryReversed in new[] { false, true })
                foreach (bool secondaryReversed in new[] { false, true })
                foreach (ConnectStyle style in new[] { ConnectStyle.Straight, ConnectStyle.StepHorizontal, ConnectStyle.StepVertical })
                foreach (int scaling in new[] { 0, 1, 2 })
                foreach (int view in new[] { 0, 1, 2, 3, 4, 5 })
                {
                    fast.Rotated = rotated;
                    fast.XScale = scaling == 1 ? .7 : 1;
                    fast.XOffset = scaling == 1 ? .02 : 0;
                    fast.YScale = scaling == 2 ? -1.7 : 1;
                    fast.YOffset = scaling == 2 ? .4 : 0;
                    fast.MinimumIndex = length > 10 && view == 5 ? 3 : 0;
                    fast.MaximumIndex = length > 10 && view == 5 ? length - 4 : length - 1;
                    double end = Math.Max(xs[^1] * fast.XScale + fast.XOffset, .01);
                    (double min, double max) = view switch
                    {
                        0 => (-.1, end + .1), 1 => (end * .2, end * .8),
                        2 => (-2, -1), 3 => (end + 1, end + 2), 4 => (end * .49, end * .51), _ => (-.03, end + .03)
                    };
                    if (primaryReversed) (min, max) = (max, min);
                    double lo = secondaryReversed ? 2 : -2, hi = secondaryReversed ? -2 : 2;
                    if (rotated) plot.Axes.SetLimits(lo, hi, min, max);
                    else plot.Axes.SetLimits(min, max, lo, hi);
                    clock.Begin();
                    string label = $"{length}/{data}/simd={simd}/rot={rotated}/rev={primaryReversed},{secondaryReversed}/style={style}/scale={scaling}/view={view}";
                    IReadOnlyList<Pixel>? reference = null;
                    Exception? originalError = null;
                    try { reference = fast.Reference.GetPixelsToDraw(rp, axes, style); }
                    catch (Exception ex) { originalError = ex; }
                    IReadOnlyList<Pixel>? actual = null;
                    Exception? actualError = null;
                    try { actual = fast.GetPixelsToDraw(rp, axes, style); }
                    catch (Exception ex) { actualError = ex; }
                    if (originalError is not null)
                    {
                        if (originalError.GetType() != actualError?.GetType()) throw new InvalidOperationException(label + " exception mismatch.", actualError);
                        expectedExceptions++;
                        continue;
                    }
                    if (actualError is not null) throw new InvalidOperationException(label, actualError);
                    Compare(reference!, actual!, label);
                    comparisons++; points += reference!.Count;
                    if (scaling == 0 && view == 0 && !rotated && !primaryReversed && !secondaryReversed && style == ConnectStyle.Straight)
                    {
                        // Reuse the same arrays and count, but require fresh sampling and column searches.
                        if (length > 2) { ys[length / 2] = 543.25; xs[length / 2] = (xs[length / 2 - 1] + xs[length / 2 + 1]) * .5; }
                        clock.Begin();
                        Compare(fast.Reference.GetPixelsToDraw(rp, axes, style), fast.GetPixelsToDraw(rp, axes, style), label + "/mutated");
                        mutations++;
                    }
                }
                vectorRanges += fast.VectorRanges; specialRanges += fast.SpecialScalarRanges;
            }
        };
        target.Render(plot);
        gpu.Context.Flush(true, false); gpu.Wait();
        Pixel[][] contours = [
            [], [new(1, 2)], [new(1, 2), new(3, 4)],
            [Pixel.NaN, new(1, 2), new(3, 4), Pixel.NaN, new(5, 6), Pixel.NaN],
            [new(2, -0f), new(2, 3), new(2, 3), new(10, 8)],
            Enumerable.Range(0, 10000).Select(i => i % 301 == 0 ? Pixel.NaN : new Pixel(i * .1f, MathF.Sin(i * .1f))).ToArray()
        ];
        if (Marshal.SizeOf<Pixel>() != Marshal.SizeOf<SKPoint>()) throw new InvalidOperationException("Bulk point layout mismatch.");
        foreach (Pixel[] contour in contours)
        {
            using SKPath reference = new ScottPlot.PathStrategies.Straight().GetPath(contour);
            using SKPath actual = new();
            BulkPathLine.Rebuild(actual, contour);
            if (reference.ToSvgPathData() != actual.ToSvgPathData() || reference.PointCount != actual.PointCount)
                throw new InvalidOperationException("Bulk path differs from Straight.");
            paths++;
        }
        return new { Result = "PASS", PointSequenceComparisons = comparisons, ComparedPoints = points,
            MatchedReferenceExceptions = expectedExceptions, InPlaceMutationComparisons = mutations, BulkPathComparisons = paths,
            VectorRanges = vectorRanges, NaNOrSignedZeroScalarRanges = specialRanges, NaNInputs = nanInputs, InfinityInputs = infinityInputs, NegativeZeroInputs = signedZeroInputs,
            Coverage = "length 1/2/3/37/20000; smooth/spikes/NaN/signed-zero/infinity/duplicate-X; rotated; both axes inverted; straight and two steps; offsets/scales incl negative Y; views and index bounds. Finite points bit-equal; NaNs compare as NaNs.",
            Limit = "Focused source/path checks, not every ScottPlot type/style or original control API." };
    }

    private static void Compare(IReadOnlyList<Pixel> reference, IReadOnlyList<Pixel> actual, string label)
    {
        if (reference.Count != actual.Count) throw new InvalidOperationException($"{label}: count {reference.Count} != {actual.Count}");
        for (int i = 0; i < reference.Count; i++)
            if (!Equal(reference[i].X, actual[i].X) || !Equal(reference[i].Y, actual[i].Y))
                throw new InvalidOperationException($"{label}: point {i}: {reference[i]} != {actual[i]}");
    }
    private static bool Equal(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b) || float.IsNaN(a) && float.IsNaN(b);
}

