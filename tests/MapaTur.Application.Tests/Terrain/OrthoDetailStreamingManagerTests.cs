using FluentAssertions;

using MapaTur.Application.Terrain;
using MapaTur.Domain.Geography;

namespace MapaTur.Application.Tests.Terrain;

/// <summary>
/// The GL-independent orchestration of detail-cell streaming (PLAN R2): each frame <see cref="OrthoDetailStreamingManager.Update"/>
/// decides the desired cells (ring + budget), enqueues composes for new ones and evicts stale residents;
/// <see cref="OrthoDetailStreamingManager.PumpComposes"/> runs a bounded number of composes and uploads the
/// results that are still wanted. Composition and the GPU are behind <see cref="IOrthoDetailComposer"/> and
/// <see cref="IOrthoDetailCellTarget"/> so the whole policy — load/compose, cancellation, teleport, eviction,
/// budget, decode errors, missing cells — is testable with fakes and no GL.
/// </summary>
public sealed class OrthoDetailStreamingManagerTests
{
    private static readonly OrthoDetailGrid Grid = new();
    private const long CellBytes = 100;
    private const long Budget = 1000;

    private sealed class FakeComposer : IOrthoDetailComposer
    {
        public readonly List<(int Ci, int Cj)> Composed = new();
        public readonly HashSet<int> ThrowFor = new();  // cell keys whose compose throws (decode error)
        public readonly HashSet<int> NullFor = new();    // cell keys with no data (fully nodata)
        public Action<int, int>? OnCompose;              // re-entrancy hook (invoked before returning)

        public byte[]? Compose(int ci, int cj)
        {
            Composed.Add((ci, cj));
            OnCompose?.Invoke(ci, cj);
            int key = new OrthoDetailGrid().CellKey(ci, cj);
            if (ThrowFor.Contains(key))
            {
                throw new InvalidOperationException("decode error");
            }

            return NullFor.Contains(key) ? null : new byte[] { (byte)ci, (byte)cj, 1, 255 };
        }
    }

    private sealed class FakeTarget : IOrthoDetailCellTarget
    {
        public readonly List<int> Uploaded = new();
        public readonly List<int> Evicted = new();
        public readonly HashSet<int> ThrowUploadFor = new();
        public readonly HashSet<int> ThrowEvictFor = new();

        public void UploadCell(int cellKey, byte[] rgba)
        {
            if (ThrowUploadFor.Contains(cellKey))
            {
                throw new InvalidOperationException("gpu upload failed");
            }

            Uploaded.Add(cellKey);
        }

        public void EvictCell(int cellKey)
        {
            if (ThrowEvictFor.Contains(cellKey))
            {
                throw new InvalidOperationException("gpu evict failed");
            }

            Evicted.Add(cellKey);
        }
    }

    private static OrthoDetailStreamingManager NewManager(
        FakeComposer composer, FakeTarget target, int hardCap = 8, double ringRadius = 1200, int cooldown = 30)
    {
        var policy = new OrthoDetailResidencyPolicy(Grid, ringRadius, fastMotionSpeedMps: 30, prefetchLeadMeters: 400);
        return new OrthoDetailStreamingManager(Grid, policy, composer, target, CellBytes, Budget, hardCap, cooldown);
    }

    private static int Key(GeoPoint p) => Grid.CellKey(Grid.CellForPoint(p).Ci, Grid.CellForPoint(p).Cj);

    private static readonly GeoPoint FocusA = new(49.235, 20.010);
    private static readonly GeoPoint FocusB = new(49.175, 20.095); // far from A → disjoint cells

    [Fact]
    public void UpdateThenPump_ComposesAndUploadsTheDesiredCells()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target);

        mgr.Update(FocusA, 0, 0, baseResidentBytes: 0);
        int pumped = mgr.PumpComposes(100);

        pumped.Should().BeGreaterThan(0);
        mgr.Resident.Should().NotBeEmpty();
        target.Uploaded.Should().BeEquivalentTo(mgr.Resident);
        mgr.Resident.Should().Contain(Grid.CellKey(Grid.CellForPoint(FocusA).Ci, Grid.CellForPoint(FocusA).Cj));
    }

    [Fact]
    public void PumpComposes_RespectsTheMaxCellsPerCallBudget()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target);
        mgr.Update(FocusA, 0, 0, 0);

        int first = mgr.PumpComposes(2);

        first.Should().Be(2);
        target.Uploaded.Should().HaveCount(2);
        mgr.QueuedCount.Should().BeGreaterThan(0); // remainder still queued
    }

    [Fact]
    public void CellLeavingDesiredBeforeCompose_IsNeitherComposedNorUploaded()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target);

        mgr.Update(FocusA, 0, 0, 0);               // queues A cells (none composed yet)
        var aCell = Grid.CellForPoint(FocusA);
        int aKey = Grid.CellKey(aCell.Ci, aCell.Cj);
        mgr.Update(FocusB, 0, 0, 0);               // camera jumped → A cells no longer desired (cancelled)
        mgr.PumpComposes(100);

        composer.Composed.Should().NotContain((aCell.Ci, aCell.Cj));
        target.Uploaded.Should().NotContain(aKey);
    }

    [Fact]
    public void Teleport_EvictsTheOldResidentsAndLoadsTheNewArea()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target, hardCap: 5, ringRadius: 900);

        mgr.Update(FocusA, 0, 0, 0);
        mgr.PumpComposes(100);
        var residentA = mgr.Resident.ToHashSet();
        residentA.Should().NotBeEmpty();

        mgr.Update(FocusB, 0, 0, 0);
        mgr.PumpComposes(100);

        mgr.Resident.Should().NotIntersectWith(residentA, "the old area's cells must be evicted after a teleport");
        foreach (int k in residentA)
        {
            target.Evicted.Should().Contain(k);
        }
    }

    [Fact]
    public void Eviction_KeepsResidentWithinTheNearCap()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target, hardCap: 3, ringRadius: 2000); // ring has > 3 cells

        mgr.Update(FocusA, 0, 0, 0);
        mgr.PumpComposes(100);

        mgr.Resident.Count.Should().BeLessThanOrEqualTo(3);
    }

    [Fact]
    public void Budget_WhenBaseFillsTheBudget_EvictsEverythingAndQueuesNothing()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target);

        mgr.Update(FocusA, 0, 0, 0);
        mgr.PumpComposes(100);
        mgr.Resident.Should().NotBeEmpty();

        mgr.Update(FocusA, 0, 0, baseResidentBytes: Budget); // base now fills the whole budget → detail cap 0
        mgr.PumpComposes(100);

        mgr.Resident.Should().BeEmpty();
        mgr.QueuedCount.Should().Be(0);
    }

    [Fact]
    public void DecodeError_ComposerThrows_DoesNotCrashAndSkipsTheCell()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target);
        var bad = Grid.CellForPoint(FocusA);
        int badKey = Grid.CellKey(bad.Ci, bad.Cj);
        composer.ThrowFor.Add(badKey);

        mgr.Update(FocusA, 0, 0, 0);
        Action pump = () => mgr.PumpComposes(100);

        pump.Should().NotThrow();
        target.Uploaded.Should().NotContain(badKey);
        mgr.Resident.Should().NotContain(badKey);
    }

    [Fact]
    public void MissingCell_ComposerReturnsNull_IsNotUploaded()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target);
        var empty = Grid.CellForPoint(FocusA);
        int emptyKey = Grid.CellKey(empty.Ci, empty.Cj);
        composer.NullFor.Add(emptyKey);

        mgr.Update(FocusA, 0, 0, 0);
        mgr.PumpComposes(100);

        target.Uploaded.Should().NotContain(emptyKey);
        mgr.Resident.Should().NotContain(emptyKey);
    }

    // ---------- adversarial review (#1 double, #2 stale upload, #3 retry loop, #4 target throw, #5 determinism)

    [Fact] // #1 — a resident cell is not recomposed or reuploaded by a later Update at the same focus.
    public void ResidentCell_IsNotRecomposedOrReuploadedByALaterUpdate()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target);

        mgr.Update(FocusA, 0, 0, 0);
        mgr.PumpComposes(100);
        int composes = composer.Composed.Count;
        int uploads = target.Uploaded.Count;

        mgr.Update(FocusA, 0, 0, 0); // same view — everything already resident
        mgr.PumpComposes(100);

        composer.Composed.Count.Should().Be(composes);
        target.Uploaded.Count.Should().Be(uploads);
    }

    [Fact] // #1 — repeated Updates before a pump must not double-queue → each cell composed at most once.
    public void RepeatedUpdatesBeforePump_DoNotDoubleQueueOrDoubleCompose()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target);

        mgr.Update(FocusA, 0, 0, 0);
        mgr.Update(FocusA, 0, 0, 0);
        mgr.Update(FocusA, 0, 0, 0);
        mgr.PumpComposes(100);

        composer.Composed.Should().OnlyHaveUniqueItems();
        target.Uploaded.Should().OnlyHaveUniqueItems();
    }

    [Fact] // #2 — a cell that leaves desired WHILE composing is not uploaded (guard the async composer needs).
    public void ComposeResult_ForACellThatLeftDesiredDuringCompose_IsNotUploaded()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target);
        int aKey = Key(FocusA);
        composer.OnCompose = (ci, cj) =>
        {
            if (Grid.CellKey(ci, cj) == aKey)
            {
                mgr.Update(FocusB, 0, 0, 0); // teleport away mid-compose → aKey no longer desired
            }
        };

        mgr.Update(FocusA, 0, 0, 0);
        mgr.PumpComposes(100);

        target.Uploaded.Should().NotContain(aKey);
        mgr.Resident.Should().NotContain(aKey);
    }

    [Fact] // #3 — a persistent decode error retries on a cooldown, never every frame, and is never permanent.
    public void ComposeError_DoesNotRetryEveryFrame_ButRetriesAfterCooldown()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target, cooldown: 2);
        int aKey = Key(FocusA);
        composer.ThrowFor.Add(aKey);
        int Attempts() => composer.Composed.Count(t => Grid.CellKey(t.Ci, t.Cj) == aKey);

        mgr.Update(FocusA, 0, 0, 0); mgr.PumpComposes(100); // tick 1: attempt → fail → cooldown until tick 3
        int afterFirst = Attempts();
        mgr.Update(FocusA, 0, 0, 0); mgr.PumpComposes(100); // tick 2: in cooldown → NOT retried
        int afterCooldownFrame = Attempts();
        mgr.Update(FocusA, 0, 0, 0); mgr.PumpComposes(100); // tick 3: cooldown expired → retried
        int afterExpiry = Attempts();

        afterFirst.Should().Be(1);
        afterCooldownFrame.Should().Be(1, "the cell must not be retried every frame");
        afterExpiry.Should().Be(2, "but it must be retried once the cooldown expires");
    }

    [Fact] // #4 — a throwing UploadCell must not crash the pump nor mark the cell resident.
    public void UploadThrow_DoesNotCrashAndLeavesResidencyConsistent()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target);
        int aKey = Key(FocusA);
        target.ThrowUploadFor.Add(aKey);

        Action pump = () => { mgr.Update(FocusA, 0, 0, 0); mgr.PumpComposes(100); };

        pump.Should().NotThrow();
        mgr.Resident.Should().NotContain(aKey);
        mgr.Resident.Should().NotBeEmpty("other cells still upload fine");
    }

    [Fact] // #4 — a throwing EvictCell must not crash and residency must already reflect the eviction.
    public void EvictThrow_DoesNotCrashAndResidencyReflectsTheEviction()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target, hardCap: 5, ringRadius: 900);
        mgr.Update(FocusA, 0, 0, 0);
        mgr.PumpComposes(100);
        var residentA = mgr.Resident.ToHashSet();
        foreach (int k in residentA)
        {
            target.ThrowEvictFor.Add(k);
        }

        Action teleport = () => { mgr.Update(FocusB, 0, 0, 0); mgr.PumpComposes(100); };

        teleport.Should().NotThrow();
        mgr.Resident.Should().NotIntersectWith(residentA); // removed from residency despite the GPU throw
    }

    [Fact] // #5 — eviction removes the least-recently-used first (deterministic order).
    public void Eviction_RemovesLeastRecentlyUsedFirst()
    {
        var composer = new FakeComposer();
        var target = new FakeTarget();
        var mgr = NewManager(composer, target, hardCap: 5, ringRadius: 900);
        mgr.Update(FocusA, 0, 0, 0);
        mgr.PumpComposes(100);
        var uploadedA = target.Uploaded.ToList(); // upload order == recency order (LRU = index 0)

        mgr.Update(FocusB, 0, 0, 0); // disjoint area → all A cells become evictable
        mgr.PumpComposes(100);

        mgr.Resident.Should().NotIntersectWith(uploadedA);   // all A evicted
        target.Evicted.Should().ContainInOrder(uploadedA);   // in least-recently-used-first order
    }

    [Fact] // #5 — the whole sequence is deterministic: identical inputs → identical upload/evict order.
    public void StreamingSequence_IsDeterministic()
    {
        static (List<int> Up, List<int> Ev) Run()
        {
            var c = new FakeComposer();
            var t = new FakeTarget();
            var m = NewManager(c, t, hardCap: 5, ringRadius: 900);
            m.Update(FocusA, 0, 0, 0); m.PumpComposes(100);
            m.Update(FocusB, 0, 0, 0); m.PumpComposes(100);
            m.Update(FocusA, 0, 0, 0); m.PumpComposes(100);
            return (t.Uploaded, t.Evicted);
        }

        (List<int> Up, List<int> Ev) a = Run();
        (List<int> Up, List<int> Ev) b = Run();

        a.Up.Should().Equal(b.Up);
        a.Ev.Should().Equal(b.Ev);
    }
}
