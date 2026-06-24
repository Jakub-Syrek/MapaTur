namespace MapaTur.Domain.Location;

/// <summary>
/// How current the resolved device location is — a pure function of how long ago the last accepted GPS fix
/// was, computed by the location resolver. Drives how the marker is shown (solid, faded, "signal lost") without
/// ever blanking it once a fix exists.
/// </summary>
public enum LocationFreshness
{
    /// <summary>No fix has ever been accepted — show an "acquiring GPS" state, no marker.</summary>
    None = 0,

    /// <summary>A recent fix; the marker is trustworthy and shown solid.</summary>
    Live = 1,

    /// <summary>No fresh fix lately (e.g. in a gorge / under cliffs) — the last-known position is shown, flagged.</summary>
    Stale = 2,

    /// <summary>The fix is old enough that it may no longer reflect reality — last-known shown dimmed, "signal lost".</summary>
    Lost = 3,
}