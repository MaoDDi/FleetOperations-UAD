using System.Numerics;
using FleetOps.Experimental;

static class TorpedoTests
{
    private static int passed, failed;

    public static int Main()
    {
        Run("request captures multiple native pointers independently", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1, 2 }, 10);
            Equal(2, book.Entries.Count); Pending(book.Get(1, 11), 130); Pending(book.Get(2, 11), 130);
        });
        Run("request ignores zero deduplicates and keeps signed pointer identities", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 0, 1, 1, -1, 0 }, 10);
            Equal(2, book.Entries.Count); False(book.IsPending(0, 11)); True(book.IsPending(-1, 11));
        });
        Run("empty requests do not expand or clear an existing capture", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1 }, 10);
            book.Request(Array.Empty<long>(), 20); book.Request(new long[] { 0 }, 20);
            Equal(1, book.Entries.Count); Pending(book.Get(1, 21), 130);
        });
        Run("captured ID enumeration cannot expand after request", () =>
        {
            var ids = new List<long> { 1, 2 }; var book = new TorpedoVolleyBook(); book.Request(ids, 0);
            ids.Add(3); Equal(2, book.PendingIds.Count); False(book.IsPending(3, 1));
        });
        Run("get expires at exactly 120 seconds and leaves later orders pending", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1 }, 10); book.Request(new long[] { 2 }, 20);
            Pending(book.Get(1, 129.999), 130); Equal<TorpedoPending?>(null, book.Get(1, 130));
            Equal(1, book.Entries.Count); Pending(book.Get(2, 130), 140);
        });
        Run("is pending applies exact expiry and prunes the entry", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1 }, 0);
            True(book.IsPending(1, 119.999)); False(book.IsPending(1, 120)); Equal(0, book.Entries.Count);
        });
        Run("repeated request replaces deadline without stacking or touching others", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1, 2 }, 0);
            book.Request(new long[] { 1, 1 }, 20); Equal(2, book.Entries.Count);
            Pending(book.Get(1, 121), 140); Equal<TorpedoPending?>(null, book.Get(2, 121));
        });
        Run("was fired consumes only the actual firing ship", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1, 2 }, 10); book.WasFired(1);
            False(book.IsPending(1, 11)); Pending(book.Get(2, 11), 130);
            book.WasFired(1); book.WasFired(99); book.WasFired(0); Equal(1, book.Entries.Count);
        });
        Run("clear selected leaves unselected pending and empty clear is inert", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1, 2, 3 }, 10);
            book.Clear(new long[] { 0, 1, 1, 99 }); book.Clear(Array.Empty<long>());
            False(book.IsPending(1, 11)); Pending(book.Get(2, 11), 130); Pending(book.Get(3, 11), 130);
        });
        Run("clear all removes the complete independent queue", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1, 2 }, 10); book.Clear();
            Equal(0, book.Entries.Count); Equal(0, book.PendingIds.Count); False(book.IsPending(2, 11));
        });
        Run("nonfinite timestamp rejects request and reads without mutation", () =>
        {
            foreach (double now in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var book = new TorpedoVolleyBook(); book.Request(new long[] { 1 }, 10);
                Throws<ArgumentException>(() => book.Request(new long[] { 1, 2 }, now));
                Throws<ArgumentException>(() => book.Get(1, now)); Throws<ArgumentException>(() => book.IsPending(1, now));
                Equal(1, book.Entries.Count); Pending(book.Get(1, 11), 130);
            }
        });
        Run("interrupted request leaves original deadlines and new IDs untouched", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1 }, 10);
            Throws<InvalidOperationException>(() => book.Request(InterruptedIds(), 20));
            Equal(1, book.Entries.Count); Pending(book.Get(1, 21), 130); False(book.IsPending(2, 21));
        });
        Run("interrupted selected clear cannot partially remove entries", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1, 2 }, 10);
            Throws<InvalidOperationException>(() => book.Clear(InterruptedIds()));
            Equal(2, book.Entries.Count); Pending(book.Get(1, 11), 130); Pending(book.Get(2, 11), 130);
        });
        Run("null capture rejects before modifying existing entries", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1 }, 10);
            Throws<ArgumentNullException>(() => book.Request(null!, 20)); Throws<ArgumentNullException>(() => book.Clear(null!));
            Equal(1, book.Entries.Count); Pending(book.Get(1, 21), 130);
        });
        Run("entry snapshots are immutable and keep old captured deadlines", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1 }, 10); var snapshot = book.Entries;
            if (snapshot is IDictionary<long, TorpedoPending> writable)
                Throws<NotSupportedException>(() => writable.Add(9, new(140)));
            book.Request(new long[] { 1, 2 }, 20); book.WasFired(1);
            Equal(1, snapshot.Count); Pending(snapshot[1], 130); False(snapshot.ContainsKey(2));
            Equal(1, book.Entries.Count); Pending(book.Get(2, 21), 140); False(book.IsPending(9, 21));
        });
        Run("pending IDs are stable read only snapshots", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1 }, 10); var snapshot = book.PendingIds;
            if (snapshot is ICollection<long> writable) Throws<NotSupportedException>(() => writable.Add(9));
            book.Request(new long[] { 2 }, 20); book.Clear(new long[] { 1 });
            Equal(1, snapshot.Count); Equal(1L, snapshot.Single()); Equal(1, book.PendingIds.Count); Equal(2L, book.PendingIds.Single());
        });
        Run("snapshot enumeration allows native per ship consumption", () =>
        {
            var book = new TorpedoVolleyBook(); book.Request(new long[] { 1, 2, 3 }, 10); int fired = 0;
            foreach (var entry in book.Entries) { book.WasFired(entry.Key); fired++; }
            Equal(3, fired); Equal(0, book.Entries.Count);
        });
        Run("static and following public aim updates do not cancel torpedo pending", () =>
        {
            var guns = new FleetOrderBook(); var torpedoes = new TorpedoVolleyBook();
            guns.Aim(new long[] { 1 }, new Vector3(10, 0, 20), 0); torpedoes.Request(new long[] { 1 }, 10);
            guns.Aim(new long[] { 1 }, new Vector3(30, 0, 40), 11);
            guns.ToggleFollow(new long[] { 1 }, Vector3.Zero, 12); guns.UpdateFollow(new Vector3(50, 0, 60), 13);
            Pending(torpedoes.Get(1, 13), 130); Equal(new Vector3(50, 0, 60), guns.Get(1, 13)!.Point!.Value);
        });
        Run("torpedo firing cannot consume a gun volley or following state", () =>
        {
            var guns = new FleetOrderBook(); var torpedoes = new TorpedoVolleyBook();
            guns.ToggleFollow(new long[] { 1 }, Vector3.Zero, 0); guns.Fire(new long[] { 1 }, 10);
            torpedoes.Request(new long[] { 1 }, 10); torpedoes.WasFired(1);
            Equal(FleetOrderMode.FirePoint, guns.Get(1, 11)!.Mode); True(guns.IsFollowing(1));
            Equal(0, torpedoes.Entries.Count);
        });
        Run("explicit selected restore clears both books without widening the set", () =>
        {
            var guns = new FleetOrderBook(); var torpedoes = new TorpedoVolleyBook();
            guns.ToggleFollow(new long[] { 1, 2 }, Vector3.Zero, 0); torpedoes.Request(new long[] { 1, 2 }, 10);
            guns.Restore(new long[] { 1 }); torpedoes.Clear(new long[] { 1 });
            Equal<FleetOrder?>(null, guns.Get(1, 11)); False(torpedoes.IsPending(1, 11));
            True(guns.IsFollowing(2)); Pending(torpedoes.Get(2, 11), 130);
        });
        Run("gun static follow and capture changes cannot alter independent torpedo aim", () =>
        {
            var guns = new FleetOrderBook(); var torps = new FleetOrderBook(); var queue = new TorpedoVolleyBook();
            torps.Aim(new long[] { 1 }, new Vector3(100, 0, 200), 0);
            torps.ToggleFollow(new long[] { 2 }, new Vector3(300, 0, 400), 0);
            queue.Request(new long[] { 1, 2 }, 10);
            guns.Aim(new long[] { 1, 3 }, new Vector3(10, 0, 20), 11);
            guns.ToggleFollow(new long[] { 1, 3 }, new Vector3(30, 0, 40), 12);
            guns.UpdateFollow(new Vector3(50, 0, 60), 13);
            guns.Aim(new long[] { 3 }, new Vector3(70, 0, 80), 14);
            Equal(2, torps.Entries.Count); False(torps.IsFollowing(1)); True(torps.IsFollowing(2));
            Equal(new Vector3(100, 0, 200), torps.Get(1, 14)!.Point!.Value);
            Equal(new Vector3(300, 0, 400), torps.Get(2, 14)!.Point!.Value);
            Equal<FleetOrder?>(null, torps.Get(3, 14));
            True(guns.IsFollowing(1)); False(guns.IsFollowing(3));
            Pending(queue.Get(1, 14), 130); Pending(queue.Get(2, 14), 130);
        });
        Run("torpedo follow off holds its last point without disabling gun follow", () =>
        {
            var guns = new FleetOrderBook(); var torps = new FleetOrderBook(); var queue = new TorpedoVolleyBook();
            guns.ToggleFollow(new long[] { 1, 3 }, new Vector3(10, 0, 20), 0);
            torps.ToggleFollow(new long[] { 1, 2 }, new Vector3(100, 0, 200), 0);
            queue.Request(new long[] { 1, 2 }, 10); torps.UpdateFollow(new Vector3(300, 0, 400), 11);
            False(torps.ToggleFollow(new long[] { 1, 2 }, Vector3.Zero, 12));
            guns.UpdateFollow(new Vector3(50, 0, 60), 13); torps.UpdateFollow(new Vector3(500, 0, 600), 13);
            Equal(0, torps.FollowingIds.Count); True(guns.IsFollowing(1)); True(guns.IsFollowing(3));
            Equal(new Vector3(50, 0, 60), guns.Get(1, 13)!.Point!.Value);
            Equal(new Vector3(50, 0, 60), guns.Get(3, 13)!.Point!.Value);
            Equal(new Vector3(300, 0, 400), torps.Get(1, 13)!.Point!.Value);
            Equal(new Vector3(300, 0, 400), torps.Get(2, 13)!.Point!.Value);
            Pending(queue.Get(1, 13), 130); Pending(queue.Get(2, 13), 130);
        });
        Run("torpedo consumption preserves gun pending volley and both independent follows", () =>
        {
            var guns = new FleetOrderBook(); var torps = new FleetOrderBook(); var queue = new TorpedoVolleyBook();
            guns.ToggleFollow(new long[] { 1 }, new Vector3(10, 0, 20), 0); guns.Fire(new long[] { 1 }, 10);
            torps.ToggleFollow(new long[] { 1 }, new Vector3(100, 0, 200), 0); queue.Request(new long[] { 1, 2 }, 10);
            torps.UpdateFollow(new Vector3(300, 0, 400), 11); queue.WasFired(1);
            var gunOrder = guns.Get(1, 11)!;
            Equal(FleetOrderMode.FirePoint, gunOrder.Mode); Equal(130d, gunOrder.ExpiresAt);
            Equal(new Vector3(10, 0, 20), gunOrder.Point!.Value);
            True(guns.IsFollowing(1)); True(torps.IsFollowing(1));
            False(queue.IsPending(1, 11)); Pending(queue.Get(2, 11), 130);
            guns.UpdateFollow(new Vector3(50, 0, 60), 12);
            Equal(FleetOrderMode.FirePoint, guns.Get(1, 12)!.Mode); Equal(130d, guns.Get(1, 12)!.ExpiresAt);
            Equal(new Vector3(50, 0, 60), guns.Get(1, 12)!.Point!.Value);
            Equal(new Vector3(300, 0, 400), torps.Get(1, 12)!.Point!.Value);
        });
        Console.WriteLine($"TOTAL {passed + failed}; PASS {passed}; FAIL {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static IEnumerable<long> InterruptedIds() { yield return 1; yield return 2; throw new InvalidOperationException("capture interrupted"); }
    private static void Pending(TorpedoPending? value, double expiry) { True(value != null); Equal(expiry, value!.ExpiresAt); }
    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}"); }
    }
    private static void True(bool value) { if (!value) throw new Exception("expected true, got false"); }
    private static void False(bool value) { if (value) throw new Exception("expected false, got true"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}"); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"expected {typeof(T).Name}");
    }
}
