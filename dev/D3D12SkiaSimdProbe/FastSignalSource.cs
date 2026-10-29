using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using ScottPlot;
using ScottPlot.DataSources;

/// <summary>Owns a reusable point array with an explicit logical length.</summary>
internal sealed class ReusedPixels : IReadOnlyList<Pixel>
{
    internal Pixel[] Storage = [];
    /// <inheritdoc />
    public int Count { get; private set; }
    /// <inheritdoc />
    public Pixel this[int index] => Storage[index];
    internal ReadOnlySpan<Pixel> Span => Storage.AsSpan(0, Count);
    internal void Prepare(int capacity)
    {
        if (Storage.Length < capacity) Storage = new Pixel[capacity];
        Count = 0;
    }
    internal void Add(Pixel pixel) => Storage[Count++] = pixel;
    internal void Set(int index, Pixel pixel) => Storage[index] = pixel;
    /// <inheritdoc />
    public IEnumerator<Pixel> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return Storage[i];
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Invalidates X-column metadata for every full Figure, including in-place X updates.</summary>
internal sealed class FrameClock
{
    internal long Epoch;
    internal void Begin() => Epoch++;
}

/// <summary>Reuses binary-search results across sources sharing exactly the same X array and view, only within one frame.</summary>
internal sealed class ColumnPlan
{
    private long Epoch = -1;
    private PlanKey Key;
    private double[]? Xs;
    internal int[] First = [], Last = [], Number = [];
    internal int Columns, Before = -1, After = -1;
    internal bool Reversed;
    internal int Builds;

    internal void Ensure(FrameClock clock, double[] xs, FastSignalSource source, RenderPack rp, IAxes axes)
    {
        double min = source.Rotated ? axes.YAxis.Min : axes.XAxis.Min;
        double max = source.Rotated ? axes.YAxis.Max : axes.XAxis.Max;
        double span = source.Rotated ? axes.YAxis.Range.Span : axes.XAxis.Range.Span;
        double unitsPerPixel = source.Rotated ? axes.YAxis.Height / rp.DataRect.Height : axes.XAxis.Width / rp.DataRect.Width;
        int columns = (int)Math.Ceiling(source.Rotated ? rp.DataRect.Height : rp.DataRect.Width);
        PlanKey key = new(source.MinimumIndex, source.MaximumIndex, source.XScale, source.XOffset, min, max, span, unitsPerPixel, columns, source.Rotated);
        if (Epoch == clock.Epoch && ReferenceEquals(Xs, xs) && Key == key) return;
        Epoch = clock.Epoch; Xs = xs; Key = key; Builds++;
        Before = After = -1; Reversed = span < 0; Columns = 0;
        if (xs.Length == 0) return;

        int first = source.MinimumIndex, last = source.MaximumIndex;
        if (xs.Length != 1)
        {
            int firstFound = Search(xs, source.MinimumIndex, source.MaximumIndex, span > 0 ? min : max, source.XScale, source.XOffset).Searched;
            if (firstFound > source.MinimumIndex) { Before = firstFound - 1; first = firstFound; }
            int lastFound = Search(xs, source.MinimumIndex, source.MaximumIndex, span > 0 ? max : min, source.XScale, source.XOffset).Searched;
            if (lastFound <= source.MaximumIndex) { After = lastFound; last = lastFound - 1; }
        }
        IndexRange visible = new(first, last);
        if (visible.IsValid && xs[first] > xs[last]) throw new InvalidDataException("Xs must contain ascending values.");
        if (visible.Length <= 0) return;
        if (First.Length < columns) { First = new int[columns]; Last = new int[columns]; Number = new int[columns]; }
        Columns = columns;
        for (int col = 0; col < columns; col++)
        {
            double start = min + unitsPerPixel * col;
            double end = start + unitsPerPixel;
            if (source.Rotated) end += unitsPerPixel * .01;
            int a = Search(xs, first, last, start, source.XScale, source.XOffset).Searched;
            int b = Search(xs, first, last, end, source.XScale, source.XOffset).Searched;
            Number[col] = Math.Abs(b - a);
            First[col] = a < b ? a : a - 1;
            Last[col] = a < b ? b - 1 : b;
        }
    }

    private static (int Searched, int Limited) Search(double[] xs, int min, int max, double value, double scale, double offset)
    {
        int index = Array.BinarySearch(xs, min, max - min + 1, (value - offset) / scale);
        if (index < 0) index = ~index;
        return (index, index > max ? max : index);
    }

    private readonly record struct PlanKey(int MinIndex, int MaxIndex, double XScale, double XOffset,
        double AxisMin, double AxisMax, double AxisSpan, double UnitsPerPixel, int Columns, bool Rotated);
}

/// <summary>Probes reusable CPU sampling and SIMD through the existing public ISignalXYSource contract.</summary>
/// <remarks>It rescans Y data every call and rebuilds X-column metadata every frame. It does not cache a rendered image.</remarks>
internal sealed class FastSignalSource : ISignalXYSource
{
    internal readonly double[] Xs, Ys;
    internal readonly SignalXYSourceDoubleArray Reference;
    private readonly FrameClock Clock;
    private readonly ColumnPlan Plan;
    private readonly bool Simd;
    internal readonly ReusedPixels Pixels = new();
    internal long DataTicks, VectorRanges, SpecialScalarRanges;

    internal FastSignalSource(double[] xs, double[] ys, FrameClock clock, ColumnPlan? shared, bool simd)
    {
        Xs = xs; Ys = ys; Clock = clock; Plan = shared ?? new ColumnPlan(); Simd = simd;
        Reference = new SignalXYSourceDoubleArray(xs, ys);
        if (simd && !Vector256.IsHardwareAccelerated) throw new PlatformNotSupportedException("Vector256 acceleration required for the SIMD diagnostic.");
    }
    /// <inheritdoc />
    public int Count => Reference.Count;
    /// <inheritdoc />
    public double XOffset { get => Reference.XOffset; set => Reference.XOffset = value; }
    /// <inheritdoc />
    public double YOffset { get => Reference.YOffset; set => Reference.YOffset = value; }
    /// <inheritdoc />
    public double XScale { get => Reference.XScale; set => Reference.XScale = value; }
    /// <inheritdoc />
    public double YScale { get => Reference.YScale; set => Reference.YScale = value; }
    /// <inheritdoc />
    public int MinimumIndex { get => Reference.MinimumIndex; set => Reference.MinimumIndex = value; }
    /// <inheritdoc />
    public int MaximumIndex { get => Reference.MaximumIndex; set => Reference.MaximumIndex = value; }
    /// <inheritdoc />
    public bool Rotated { get => Reference.Rotated; set => Reference.Rotated = value; }
    /// <inheritdoc />
    public AxisLimits GetAxisLimits() => Reference.GetAxisLimits();
    /// <inheritdoc />
    public DataPoint GetNearest(Coordinates location, RenderDetails renderInfo, float maxDistance = 15) => Reference.GetNearest(location, renderInfo, maxDistance);
    /// <inheritdoc />
    public DataPoint GetNearestX(Coordinates location, RenderDetails renderInfo, float maxDistance = 15) => Reference.GetNearestX(location, renderInfo, maxDistance);

    /// <inheritdoc />
    public IReadOnlyList<Pixel> GetPixelsToDraw(RenderPack rp, IAxes axes, ConnectStyle connectStyle)
    {
        long begin = System.Diagnostics.Stopwatch.GetTimestamp();
        Plan.Ensure(Clock, Xs, this, rp, axes);
        Pixels.Prepare(4 * (int)Math.Ceiling(Rotated ? rp.DataRect.Height : rp.DataRect.Width) + 2);
        if (Count == 0) return Pixels;
        int before = Plan.Reversed ? Plan.After : Plan.Before;
        int after = Plan.Reversed ? Plan.Before : Plan.After;
        if (before >= 0) Pixels.Add(PointAt(before, axes));
        for (int col = 0; col < Plan.Columns; col++)
        {
            int count = Plan.Number[col];
            if (count == 0) continue;
            int first = Plan.First[col], last = Plan.Last[col];
            float primary = Rotated ? rp.DataRect.Bottom - col : rp.DataRect.Left + col;
            AddValue(primary, Ys[first] * YScale + YOffset, axes);
            if (count > 2)
            {
                (double min, double max) = Range(first, last);
                CoordinateRange scaled = new(min * YScale + YOffset, max * YScale + YOffset); min = scaled.Min; max = scaled.Max;
                if (Ys[first] > Ys[last]) { AddValue(primary, max, axes); AddValue(primary, min, axes); }
                else { AddValue(primary, min, axes); AddValue(primary, max, axes); }
            }
            if (count > 1) AddValue(primary, Ys[last] * YScale + YOffset, axes);
        }
        if (after >= 0) Pixels.Add(PointAt(after, axes));
        if (before >= 0)
        {
            if (Rotated) BufferInterpolation.InterpolateBeforeY(rp, Pixels, connectStyle);
            else BufferInterpolation.InterpolateBeforeX(rp, Pixels, connectStyle);
        }
        if (after >= 0)
        {
            if (Rotated) BufferInterpolation.InterpolateAfterY(rp, Pixels, connectStyle);
            else BufferInterpolation.InterpolateAfterX(rp, Pixels, connectStyle);
        }
        DataTicks += System.Diagnostics.Stopwatch.GetTimestamp() - begin;
        return Pixels;
    }

    private Pixel PointAt(int index, IAxes axes)
    {
        double x = Xs[index] * XScale + XOffset, y = Ys[index] * YScale + YOffset;
        return Rotated ? new Pixel(axes.GetPixelX(y), axes.GetPixelY(x)) : new Pixel(axes.GetPixelX(x), axes.GetPixelY(y));
    }
    private void AddValue(float primary, double value, IAxes axes) =>
        Pixels.Add(Rotated ? new Pixel(axes.GetPixelX(value), primary) : new Pixel(primary, axes.GetPixelY(value)));

    private (double Min, double Max) Range(int first, int last)
    {
        int start = Math.Min(first, last), end = Math.Max(first, last);
        if (!Simd || end - start + 1 < Vector256<double>.Count * 2) return ScalarRange(start, end);
        Vector256<double> vmin = Vector256.Create(double.PositiveInfinity);
        Vector256<double> vmax = Vector256.Create(double.NegativeInfinity);
        Vector256<double> special = Vector256<double>.Zero;
        ref double data = ref MemoryMarshal.GetArrayDataReference(Ys);
        int i = start;
        for (; i <= end - Vector256<double>.Count + 1; i += Vector256<double>.Count)
        {
            Vector256<double> values = Vector256.LoadUnsafe(ref data, (nuint)i);
            special |= ~Vector256.Equals(values, values);
            special |= Vector256.Equals(values, Vector256<double>.Zero);
            vmin = Vector256.Min(vmin, values);
            vmax = Vector256.Max(vmax, values);
        }
        // Preserve the scalar reference's NaN and signed-zero semantics, rather than relying on ISA-specific min/max ties.
        if (!Vector256.EqualsAll(special, Vector256<double>.Zero))
        {
            SpecialScalarRanges++;
            return ScalarRange(start, end);
        }
        VectorRanges++;
        var lowMin = Vector128.Min(vmin.GetLower(), vmin.GetUpper());
        var lowMax = Vector128.Max(vmax.GetLower(), vmax.GetUpper());
        double min = Math.Min(lowMin.GetElement(0), lowMin.GetElement(1));
        double max = Math.Max(lowMax.GetElement(0), lowMax.GetElement(1));
        for (; i <= end; i++) { min = Math.Min(Ys[i], min); max = Math.Max(Ys[i], max); }
        return (min, max);
    }

    private (double Min, double Max) ScalarRange(int start, int end)
    {
        double min = Ys[start], max = Ys[start];
        for (int i = start; i <= end; i++) { min = Math.Min(Ys[i], min); max = Math.Max(Ys[i], max); }
        return (min, max);
    }
}

