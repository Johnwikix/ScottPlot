using ScottPlot;
using SkiaSharp;

/// <summary>Checks the no-op marker guard against visible, outlined, hidden, and custom-rendered cases.</summary>
internal static class MarkerChecks
{
    internal static object Run(GpuHarness gpu, GpuTarget target, string evidence)
    {
        List<object> results = [];
        foreach (string name in new[] { "default-zero", "visible-circle", "visible-square", "zero-with-outline", "zero-with-hairline", "custom-zero", "hidden-visible-size" })
        {
            byte[]? reference = null;
            int customCalls = 0;
            foreach (bool skip in new[] { false, true })
            {
                using FigureFixture figure = new(new("marker-check", true, true, true, true, skip));
                MarkerStyle marker = figure.Signals[0].MarkerStyle;
                CountingMarker? custom = null;
                switch (name)
                {
                    case "visible-circle": marker.Size = 5; break;
                    case "visible-square": marker.Size = 5; marker.Shape = MarkerShape.FilledSquare; break;
                    case "zero-with-outline": marker.OutlineWidth = 2; break;
                    case "zero-with-hairline": marker.OutlineStyle.Hairline = true; break;
                    case "custom-zero": marker.CustomRenderer = custom = new(); break;
                    case "hidden-visible-size": marker.IsVisible = false; marker.Size = 5; break;
                }
                bool guarded = BulkPathLine.CanSkipZeroMarker(marker);
                if (guarded != (name == "default-zero")) throw new InvalidOperationException("Marker guard mismatch: " + name);
                for (int i = 0; i < 3; i++) { target.Render(figure.Plot); gpu.Context.Flush(true, false); gpu.Wait(); }
                byte[] pixels = target.DiagnosticPixels(Path.Combine(evidence, $"marker-{name}-{skip}.png"));
                if (reference is null) { reference = pixels; customCalls = custom?.Calls ?? 0; }
                else
                {
                    if (!pixels.SequenceEqual(reference)) throw new InvalidOperationException("Marker image mismatch: " + name);
                    if (custom is not null && (custom.Calls != customCalls || custom.Calls == 0)) throw new InvalidOperationException("Custom marker was skipped.");
                    results.Add(new { Name = name, Guarded = guarded, DifferentBytes = 0, CustomCallsPerVariant = custom?.Calls ?? 0 });
                }
            }
        }
        return new { Result = "PASS", DiagnosticReadbacksOutsideTiming = 14, Cases = results,
            Scope = "Focused default no-op guard. Hatched fills are conservatively excluded by the guard; exhaustive styles still unverified." };
    }

    /// <summary>Renders a visible circle even when requested size is zero, to ensure custom renderers are preserved.</summary>
    private sealed class CountingMarker : IMarker
    {
        internal int Calls;
        /// <inheritdoc />
        public void Render(SKCanvas canvas, Paint paint, Pixel center, float size, MarkerStyle markerStyle)
        {
            Calls++;
            paint.IsStroke = false; paint.Color = Colors.Red;
            canvas.DrawCircle(center.X, center.Y, 2, paint.SKPaint);
        }
    }
}
