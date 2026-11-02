using Avalonia;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace AvaloniaD3D12Probe;

/// <summary>Observes the real Avalonia drawing lease without reading any pixels.</summary>
internal sealed class BackendWitness : Avalonia.Controls.Control
{
    internal string? Backend;
    internal long Observations;

    /// <inheritdoc />
    public override void Render(DrawingContext context) => context.Custom(new Operation(this, new(Bounds.Size)));

    /// <summary>Records whether the active window canvas has a hardware GPU context.</summary>
    private sealed class Operation(BackendWitness owner, Rect bounds) : ICustomDrawOperation
    {
        /// <inheritdoc />
        public Rect Bounds => bounds;
        /// <inheritdoc />
        public void Dispose() { }
        /// <inheritdoc />
        public bool HitTest(Point p) => false;
        /// <inheritdoc />
        public bool Equals(ICustomDrawOperation? other) => false;
        /// <inheritdoc />
        public void Render(ImmediateDrawingContext context)
        {
            using var lease = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()?.Lease()
                ?? throw new InvalidOperationException("No Skia lease on the window canvas.");
            if (lease.GrContext is null || lease.GrContext.IsAbandoned)
                throw new InvalidOperationException("The probe requires a GPU-backed Avalonia canvas.");
            owner.Backend = lease.GrContext.Backend.ToString();
            Interlocked.Increment(ref owner.Observations);
            using SKPaint paint = new() { Color = SKColors.Green };
            lease.SkCanvas.DrawRect(0, 0, 6, 6, paint);
        }
    }
}
