using FleetOps.Experimental;

static class GunVolleyTests
{
    static int passed, failed;
    static GunPartShots P(long id, long shots) => new(id, shots);
    static GunVolleyBook Two()
    {
        var b = new GunVolleyBook(); b.Request(1, new[] { P(10, 20), P(11, 30) }); return b;
    }
    public static int Main()
    {
        Run("first turret does not consume delayed second turret", () =>
        {
            var b = Two(); var r = b.Observe(1, new[] { P(10, 24), P(11, 30) });
            Equal(false, r.AllCompleted); Ids(r.NewlyCompletedIds, 10); Ids(r.CompletedIds, 10);
            Equal(false, b.Entries[1][11].Completed);
        });
        Run("delayed second turret completes whole capture", () =>
        {
            var b = Two(); b.Observe(1, new[] { P(10, 24) });
            var r = b.Observe(1, new[] { P(11, 33) });
            Equal(true, r.AllCompleted); Ids(r.NewlyCompletedIds, 11); Ids(r.CompletedIds, 10, 11);
        });
        Run("any positive delta is legal without imposing barrel count", () =>
        {
            foreach (var delta in new long[] { 1, 3, 4 })
            {
                var b = new GunVolleyBook(); b.Request(1, new[] { P(10, 100) });
                Equal(true, b.Observe(1, new[] { P(10, 100 + delta) }).AllCompleted);
            }
        });
        Run("unchanged or decreasing shots cannot mark fired", () =>
        {
            var b = Two(); var r = b.Observe(1, new[] { P(10, 20), P(11, 29) });
            Equal(false, r.AllCompleted); Ids(r.CompletedIds); Ids(r.NewlyCompletedIds);
        });
        Run("completion is sticky and emitted only once", () =>
        {
            var b = Two(); b.Observe(1, new[] { P(10, 24) });
            var r = b.Observe(1, new[] { P(10, 28) }); Ids(r.NewlyCompletedIds); Ids(r.CompletedIds, 10);
            r = b.Observe(1, new[] { P(10, 0), P(11, 31) }); Equal(true, r.AllCompleted);
            r = b.Observe(1, Array.Empty<GunPartShots>()); Equal(true, r.AllCompleted); Ids(r.NewlyCompletedIds);
        });
        Run("uncaptured parts and missing captured parts cannot complete whole ship", () =>
        {
            var b = Two(); var r = b.Observe(1, new[] { P(99, 999), P(10, 24) });
            Equal(false, r.AllCompleted); Equal(2, b.Entries[1].Count); Ids(r.CompletedIds, 10);
        });
        Run("no ledger is never vacuously complete", () =>
        {
            var b = new GunVolleyBook(); var r = b.Observe(1, new[] { P(10, 99) });
            Equal(false, r.AllCompleted); Ids(r.CompletedIds); Equal(0, b.Entries.Count);
        });
        Run("ships observe independently even with matching part identities", () =>
        {
            var b = Two(); b.Request(2, new[] { P(10, 0) });
            Equal(true, b.Observe(2, new[] { P(10, 1) }).AllCompleted);
            Ids(b.CompletedPartIds(1)); Equal(false, b.Entries[1][10].Completed);
        });
        Run("repeated fire replaces capture and resets completed mask", () =>
        {
            var b = Two(); b.Observe(1, new[] { P(10, 24) });
            b.Request(1, new[] { P(11, 30), P(12, 8) }); Ids(b.CompletedPartIds(1));
            Equal(false, b.Entries[1].ContainsKey(10));
            Equal(false, b.Observe(1, new[] { P(10, 100), P(11, 31) }).AllCompleted);
        });
        Run("capture filters zero preserves signed identities and deduplicates first", () =>
        {
            var b = new GunVolleyBook(); Equal(true, b.Request(-1, new[] { P(0, 0), P(10, 20), P(10, 999), P(-2, 5) }));
            Equal(2, b.Entries[-1].Count); Equal(20L, b.Entries[-1][10].BaselineShots);
            Equal(true, b.Observe(-1, new[] { P(10, 21), P(-2, 6) }).AllCompleted);
        });
        Run("observation duplicate pointer preserves first reading", () =>
        {
            var b = Two(); var r = b.Observe(1, new[] { P(10, 20), P(10, 24) });
            Ids(r.CompletedIds); Equal(false, r.AllCompleted);
        });
        Run("empty or all zero recapture removes only that ship and rejects request", () =>
        {
            var b = Two(); b.Request(2, new[] { P(20, 0) });
            Equal(false, b.Request(1, Array.Empty<GunPartShots>())); Equal(false, b.Entries.ContainsKey(1));
            Equal(false, b.Request(2, new[] { P(0, 0) })); Equal(0, b.Entries.Count);
        });
        Run("zero ship never enters ledger", () =>
        {
            var b = new GunVolleyBook(); Equal(false, b.Request(0, new[] { P(10, 0) }));
            Equal(false, b.Observe(0, new[] { P(10, 1) }).AllCompleted); Equal(0, b.Entries.Count);
        });
        Run("captured input cannot expand or rewrite baselines later", () =>
        {
            var b = new GunVolleyBook(); var list = new List<GunPartShots> { P(10, 5) }; b.Request(1, list);
            list[0] = P(10, 999); list.Add(P(11, 0));
            Equal(true, b.Observe(1, new[] { P(10, 6) }).AllCompleted); Equal(1, b.Entries[1].Count);
        });
        Run("interrupted recapture leaves old ledger atomically intact", () =>
        {
            var b = Two(); Throws<InvalidOperationException>(() => b.Request(1, BrokenParts(P(12, 0))));
            Equal(2, b.Entries[1].Count); Equal(true, b.Entries[1].ContainsKey(10));
        });
        Run("interrupted observation cannot mark first yielded part completed", () =>
        {
            var b = Two(); Throws<InvalidOperationException>(() => b.Observe(1, BrokenParts(P(10, 24))));
            Ids(b.CompletedPartIds(1));
        });
        Run("negative baseline rejects entire recapture", () =>
        {
            var b = Two(); Throws<ArgumentOutOfRangeException>(() => b.Request(1, new[] { P(12, 0), P(13, -1) }));
            Equal(true, b.Entries[1].ContainsKey(10)); Equal(false, b.Entries[1].ContainsKey(12));
        });
        Run("negative observation rejects before any completion", () =>
        {
            var b = Two(); Throws<ArgumentOutOfRangeException>(() => b.Observe(1, new[] { P(10, 24), P(11, -1) }));
            Ids(b.CompletedPartIds(1));
        });
        Run("null inputs reject without clearing ledger", () =>
        {
            var b = Two(); Throws<ArgumentNullException>(() => b.Request(1, null!));
            Throws<ArgumentNullException>(() => b.Observe(1, null!)); Throws<ArgumentNullException>(() => b.Clear(null!));
            Equal(2, b.Entries[1].Count);
        });
        Run("entries expose deeply immutable stable snapshots", () =>
        {
            var b = Two(); var snapshot = b.Entries;
            Throws<NotSupportedException>(() => ((IDictionary<long, IReadOnlyDictionary<long, GunVolleyPart>>)snapshot).Clear());
            Throws<NotSupportedException>(() => ((IDictionary<long, GunVolleyPart>)snapshot[1]).Clear());
            b.Observe(1, new[] { P(10, 24) }); b.Clear();
            Equal(false, snapshot[1][10].Completed); Equal(2, snapshot[1].Count); Equal(1, snapshot.Count);
        });
        Run("result IDs and gate masks are immutable stable snapshots", () =>
        {
            var b = Two(); var r = b.Observe(1, new[] { P(10, 24) }); var mask = b.CompletedPartIds(1);
            Throws<NotSupportedException>(() => ((ICollection<long>)r.NewlyCompletedIds).Add(99));
            Throws<NotSupportedException>(() => ((ICollection<long>)r.CompletedIds).Clear());
            Throws<NotSupportedException>(() => ((ICollection<long>)mask).Add(99));
            b.Observe(1, new[] { P(11, 34) }); Ids(r.CompletedIds, 10); Ids(mask, 10); Ids(b.CompletedPartIds(1), 10, 11);
        });
        Run("clear selected preserves other ship and completed gate mask", () =>
        {
            var b = Two(); b.Request(2, new[] { P(20, 0) }); b.Observe(2, new[] { P(20, 1) });
            b.Clear(new long[] { 0, 1, 1, 99 }); b.Clear(Array.Empty<long>());
            Equal(false, b.Entries.ContainsKey(1)); Ids(b.CompletedPartIds(2), 20);
        });
        Run("interrupted clear is atomic", () =>
        {
            var b = Two(); Throws<InvalidOperationException>(() => b.Clear(BrokenIds())); Equal(1, b.Entries.Count);
        });
        Run("battle reset clears every ship and mask", () =>
        {
            var b = Two(); b.Request(2, new[] { P(20, 0) }); b.Observe(1, new[] { P(10, 24) }); b.Clear();
            Equal(0, b.Entries.Count); Ids(b.CompletedPartIds(1)); Ids(b.CompletedPartIds(99));
        });
        Console.WriteLine($"RESULT {passed} PASS, {failed} FAIL, {passed + failed} total"); return failed == 0 ? 0 : 1;
    }
    static IEnumerable<GunPartShots> BrokenParts(GunPartShots first) { yield return first; throw new InvalidOperationException("native enumeration interrupted"); }
    static IEnumerable<long> BrokenIds() { yield return 1; throw new InvalidOperationException("selection enumeration interrupted"); }
    static void Run(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.GetType().Name}: {e.Message}"); } }
    static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, actual {actual}"); }
    static void Ids(IEnumerable<long> actual, params long[] expected) { if (!actual.OrderBy(x => x).SequenceEqual(expected.OrderBy(x => x))) throw new Exception($"expected IDs [{string.Join(',', expected)}], actual [{string.Join(',', actual)}]"); }
    static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new Exception("expected " + typeof(T).Name); }
}
