namespace MapaTur.Application.Terrain;

/// <summary>The work a frame's residency decision implies: cells to upload to the GPU and cells to evict.</summary>
/// <param name="ToUpload">Visible cells that are not yet resident.</param>
/// <param name="ToEvict">Resident cells dropped to stay within the budget (never a currently-visible cell).</param>
public sealed record OrthoResidencyPlan(IReadOnlyList<int> ToUpload, IReadOnlyList<int> ToEvict);

/// <summary>
/// Decides which ortho texture cells to keep resident on the GPU. Each frame it is told which cells are
/// visible; it uploads the newly-visible ones and, when the resident set exceeds the budget, evicts the
/// least-recently-rendered cells that are not needed this frame. A frame whose visible set alone exceeds
/// the budget keeps them all (you can't render a hole) — the budget is enforced only against cells that
/// have fallen out of view.
/// </summary>
public sealed class OrthoResidencyPlanner
{
    private readonly int maxResidentCells;

    // Most-recently-used order: index 0 is the least-recently-used cell, last is the most recent.
    private readonly List<int> recency = new();
    private readonly HashSet<int> resident = new();

    /// <summary>Creates a planner that targets at most <paramref name="maxResidentCells"/> resident cells.</summary>
    public OrthoResidencyPlanner(int maxResidentCells)
    {
        if (maxResidentCells <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxResidentCells), "Budget must be positive.");
        }

        this.maxResidentCells = maxResidentCells;
    }

    /// <summary>The cells currently resident on the GPU.</summary>
    public IReadOnlyCollection<int> Resident => resident;

    /// <summary>
    /// Records the cells visible this frame and returns the upload/evict work needed. Visible cells are
    /// marked most-recently-used; new ones are scheduled for upload; cells beyond the budget that are no
    /// longer visible are evicted least-recently-used first.
    /// </summary>
    public OrthoResidencyPlan Plan(IReadOnlyCollection<int> visibleCells)
    {
        ArgumentNullException.ThrowIfNull(visibleCells);

        var visibleSet = new HashSet<int>(visibleCells);

        var toUpload = new List<int>();
        foreach (int cell in visibleCells)
        {
            Touch(cell);
            if (resident.Add(cell))
            {
                toUpload.Add(cell);
            }
        }

        var toEvict = new List<int>();
        // Walk least-recently-used first, evicting non-visible cells until within budget.
        for (int i = 0; i < recency.Count && resident.Count > maxResidentCells;)
        {
            int candidate = recency[i];
            if (visibleSet.Contains(candidate))
            {
                i++;
                continue;
            }

            recency.RemoveAt(i);
            resident.Remove(candidate);
            toEvict.Add(candidate);
        }

        return new OrthoResidencyPlan(toUpload, toEvict);
    }

    // Move a cell to the most-recently-used end of the recency list.
    private void Touch(int cell)
    {
        int existing = recency.IndexOf(cell);
        if (existing >= 0)
        {
            recency.RemoveAt(existing);
        }

        recency.Add(cell);
    }
}