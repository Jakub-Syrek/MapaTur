namespace MapaTur.Application.Terrain;

/// <summary>
/// Coalesces a stream of mesh-rebuild requests (e.g. from a vertical-exaggeration slider dragged
/// rapidly) into at most one in-flight rebuild at a time, while guaranteeing the <em>last</em>
/// requested value is always built — the trailing edge.
/// <para>
/// A naive "skip while a rebuild is in flight" guard drops every request that arrives mid-build,
/// including the final one the user settles on, leaving the mesh stuck at an intermediate value.
/// This type instead stashes the latest pending value and replays it when the current build finishes.
/// </para>
/// Pure state machine, not thread-safe: call both methods from the same (UI) thread. The host owns the
/// actual background build + dispatch; this only decides <em>what</em> to build and <em>when</em>.
/// </summary>
public sealed class MeshRebuildCoalescer
{
    private bool inFlight;
    private bool hasPending;
    private double pendingValue;

    /// <summary>
    /// Records a rebuild request for <paramref name="value"/>. Returns the value to start building
    /// immediately when idle, or <see langword="null"/> when a build is already in flight — in that
    /// case the value is stashed as the trailing request (replacing any earlier pending value) and
    /// replayed by the next <see cref="CompleteRebuild"/>.
    /// </summary>
    public double? RequestRebuild(double value)
    {
        if (inFlight)
        {
            hasPending = true;
            pendingValue = value;
            return null;
        }

        inFlight = true;
        return value;
    }

    /// <summary>
    /// Signals that the in-flight rebuild has finished. Returns the latest trailing value to build next
    /// when a request arrived during the build (the coalescer stays in flight for it), or
    /// <see langword="null"/> when nothing is pending (the coalescer returns to idle).
    /// </summary>
    public double? CompleteRebuild()
    {
        if (hasPending)
        {
            hasPending = false;
            return pendingValue;
        }

        inFlight = false;
        return null;
    }
}