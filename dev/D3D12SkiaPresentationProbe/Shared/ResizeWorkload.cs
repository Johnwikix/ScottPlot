/// <summary>Produces a bounded resize workload without moving the cursor or changing foreground focus.</summary>
internal sealed class ResizeWorkload
{
    internal int Step { get; private set; }
    internal bool Next(out int width, out int height)
    {
        if (Step >= 288) { width = height = 0; return false; }
        int i = Step++ % 24;
        width = 640 + i * 27; height = 480 + i * 17;
        return true;
    }
    internal bool CycleComplete => Step != 0 && Step % 24 == 0;
}
