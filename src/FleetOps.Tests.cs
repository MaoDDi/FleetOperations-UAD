using System.Numerics;
using FleetOps.Experimental;

// Console contract tests: no game, Unity, native pointers, or external test packages.
static class Program
{
    private static int passed, failed;
    private static readonly Vector3 P = new(30, 0, -40);

    public static int Main()
    {
        Run("selection retains multiple eligible ships in source order", () =>
        {
            var result = SelectionRules.Capture(new[] { Candidate(2), Candidate(1) });
            Equal(2, result.Length); Equal(2L, result[0].Pointer); Equal(1L, result[1].Pointer);
        });
        Run("selection deduplicates pointer preserving first identity", () =>
        {
            var result = SelectionRules.Capture(new[] { Candidate(2, "first"), Candidate(2, "second"), Candidate(3) });
            Equal(2, result.Length); Equal("first", result[0].Id);
        });
        Run("selection excludes foreign dead absent empty and zero", () =>
        {
            var result = SelectionRules.Capture(new[] {
                Candidate(1) with { Owned = false }, Candidate(2) with { Alive = false },
                Candidate(3) with { InBattle = false }, Candidate(4, ""), Candidate(0), Candidate(5) });
            Equal(1, result.Length); Equal(5L, result[0].Pointer);
        });
        Run("empty selection never expands to all ships", () => Equal(0, SelectionRules.Capture(Array.Empty<FleetCandidate>()).Length));
        Run("projection intersects sea using unnormalized direction", () =>
        {
            True(SeaProjection.TryProject(new(0, 4, 0), new(6, -8, 0), 5, out var point));
            Equal(new Vector3(3, 0, 0), point);
        });
        Run("projection bounds Euclidean slant distance not depth", () =>
            False(SeaProjection.TryProject(new(0, 4, 0), new(3, -4, 0), 4.999f, out _)));
        Run("projection rejects upward horizontal and near horizon", () =>
        {
            foreach (var d in new[] { Vector3.Zero, Vector3.UnitY, Vector3.UnitX, new Vector3(1, -0.0001f, 0), new Vector3(1, -0.00001f, 0) })
                False(SeaProjection.TryProject(new(0, 4, 0), d, 1000000, out _));
        });
        Run("projection accepts downward ray past horizon threshold", () =>
            True(SeaProjection.TryProject(new(0, 0.001f, 0), new(1, -0.0002f, 0), 10, out _)));
        Run("projection rejects intersection behind camera", () =>
            False(SeaProjection.TryProject(new(0, -4, 0), new(0, -1, 0), 10, out _)));
        Run("projection rejects every nonfinite origin and direction component", () =>
        {
            foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            foreach (var vector in new[] { new Vector3(value, 4, 0), new Vector3(0, value, 0), new Vector3(0, 4, value) })
                False(SeaProjection.TryProject(vector, -Vector3.UnitY, 100, out _));
            foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            foreach (var vector in new[] { new Vector3(value, -1, 0), new Vector3(0, value, 0), new Vector3(0, -1, value) })
                False(SeaProjection.TryProject(new(0, 4, 0), vector, 100, out _));
        });
        Run("projection rejects invalid distance limits", () =>
        {
            foreach (var limit in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, float.NegativeInfinity })
                False(SeaProjection.TryProject(new(0, 4, 0), -Vector3.UnitY, limit, out _));
        });
        Run("projection handles large finite vectors without overflow", () =>
        {
            True(SeaProjection.TryProject(new(0, 4, 0), new(float.MaxValue, -float.MaxValue, 0), 6, out var point));
            Equal(new Vector3(4, 0, 0), point);
        });
        Run("aim stores persistent point for all selected nonzero ships", () =>
        {
            var book = new FleetOrderBook(); book.Aim(new long[] { 0, 1, 2, 1 }, P, 3);
            Equal(2, book.Entries.Count); Order(book.Get(1, 100000), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
            Order(book.Get(2, 100000), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
        });
        Run("fire without point tracks current target for 120 seconds", () =>
        {
            var book = new FleetOrderBook(); book.Fire(new long[] { 0, 1, 2 }, 10);
            Equal(2, book.Entries.Count); Order(book.Get(1, 129.999), FleetOrderMode.FireCurrent, null, 130);
        });
        Run("fire locks existing aim point with independent immutable value", () =>
        {
            var book = new FleetOrderBook(); var point = P;
            book.Aim(new long[] { 1 }, point, 0); book.Fire(new long[] { 1 }, 10); point.X = 999;
            Order(book.Get(1, 11), FleetOrderMode.FirePoint, P, 130);
        });
        Run("repeated fire replaces expiry instead of stacking", () =>
        {
            var book = new FleetOrderBook(); book.Aim(new long[] { 1 }, P, 0);
            book.Fire(new long[] { 1 }, 10); book.Fire(new long[] { 1 }, 20);
            Equal(1, book.Entries.Count); Order(book.Get(1, 130), FleetOrderMode.FirePoint, P, 140);
        });
        Run("new aim replaces pending fire without stacking", () =>
        {
            var book = new FleetOrderBook(); book.Fire(new long[] { 1 }, 0);
            book.Aim(new long[] { 1 }, P, 1); Equal(1, book.Entries.Count);
            Order(book.Get(1, 121), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
        });
        Run("current fire expires at exact 120 second boundary", () =>
        {
            var book = new FleetOrderBook(); book.Fire(new long[] { 1 }, 10);
            True(book.Get(1, 129.999) != null); Equal<FleetOrder?>(null, book.Get(1, 130)); Equal(0, book.Entries.Count);
        });
        Run("point fire expiry restores persistent aim at exact boundary", () =>
        {
            var book = new FleetOrderBook(); book.Aim(new long[] { 1 }, P, 0); book.Fire(new long[] { 1 }, 10);
            Order(book.Get(1, 130), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
        });
        Run("mark fired removes current fire", () =>
        {
            var book = new FleetOrderBook(); book.Fire(new long[] { 1 }, 10); book.MarkFired(1);
            Equal<FleetOrder?>(null, book.Get(1, 11));
        });
        Run("mark fired preserves point aim", () =>
        {
            var book = new FleetOrderBook(); book.Aim(new long[] { 1 }, P, 0); book.Fire(new long[] { 1 }, 10); book.MarkFired(1);
            Order(book.Get(1, 11), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
        });
        Run("mark fired leaves passive aim and unknown IDs alone", () =>
        {
            var book = new FleetOrderBook(); book.Aim(new long[] { 1 }, P, 0); book.MarkFired(1); book.MarkFired(2);
            Equal(1, book.Entries.Count); Order(book.Get(1, 3), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
        });
        Run("restore clears only listed ships", () =>
        {
            var book = new FleetOrderBook(); book.Aim(new long[] { 1, 2 }, P, 0);
            book.Restore(new long[] { 1, 99 }); Equal<FleetOrder?>(null, book.Get(1, 0));
            Order(book.Get(2, 0), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
            book.Restore(Array.Empty<long>()); Equal(1, book.Entries.Count);
        });
        Run("empty order IDs are inert", () =>
        {
            var book = new FleetOrderBook(); book.Aim(Array.Empty<long>(), P, 0); book.Fire(Array.Empty<long>(), 0);
            book.Aim(new long[] { 0 }, P, 0); book.Fire(new long[] { 0 }, 0); Equal(0, book.Entries.Count);
        });
        Run("clear removes every pending order", () =>
        {
            var book = new FleetOrderBook(); book.Aim(new long[] { 1 }, P, 0); book.Fire(new long[] { 2 }, 0);
            book.Clear(); Equal(0, book.Entries.Count); Equal<FleetOrder?>(null, book.Get(1, 0));
        });
        Run("invalid timestamps cannot partially replace orders", () =>
        {
            foreach (var now in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var book = new FleetOrderBook(); book.Aim(new long[] { 1 }, P, 0);
                ThrowsArgument(() => book.Aim(new long[] { 1, 2 }, Vector3.Zero, now));
                ThrowsArgument(() => book.Fire(new long[] { 1, 2 }, now));
                ThrowsArgument(() => book.Get(1, now));
                Equal(1, book.Entries.Count); Order(book.Get(1, 0), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
            }
        });
        Run("nonfinite points reject whole aim command", () =>
        {
            foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            foreach (var point in new[] { new Vector3(value, 0, 0), new Vector3(0, value, 0), new Vector3(0, 0, value) })
            {
                var book = new FleetOrderBook(); book.Aim(new long[] { 1 }, P, 0);
                ThrowsArgument(() => book.Aim(new long[] { 1, 2 }, point, 1));
                Equal(1, book.Entries.Count); Order(book.Get(1, 0), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
            }
        });
        Run("enumeration failure cannot partially mutate order command", () =>
        {
            var book = new FleetOrderBook(); book.Aim(new long[] { 1 }, P, 0);
            Throws<InvalidOperationException>(() => book.Aim(BrokenIds(), Vector3.Zero, 1));
            Throws<InvalidOperationException>(() => book.Fire(BrokenIds(), 1));
            Order(book.Get(1, 2), FleetOrderMode.AimPoint, P, double.PositiveInfinity); Equal(1, book.Entries.Count);
        });
        Run("entries view cannot inject a foreign order", () =>
        {
            var book = new FleetOrderBook(); book.Aim(new long[] { 1 }, P, 0);
            if (book.Entries is IDictionary<long, FleetOrder> writable)
                Throws<NotSupportedException>(() => writable.Add(2, new(FleetOrderMode.FireCurrent, null, 120)));
            Equal(1, book.Entries.Count);
        });
        Run("follow captures only submitted ships without auto fire", () =>
        {
            var book = new FleetOrderBook(); var selected = new List<long> { 1, 2, 2, 0 };
            True(book.ToggleFollow(selected, P, 0)); selected.Add(3);
            book.UpdateFollow(new Vector3(10, 0, 20), 1);
            Equal(2, book.FollowingIds.Count); Equal(2, book.Entries.Count);
            True(book.IsFollowing(1)); True(book.IsFollowing(2)); False(book.IsFollowing(3));
            Order(book.Get(1, 1), FleetOrderMode.AimPoint, new Vector3(10, 0, 20), double.PositiveInfinity);
            Equal<FleetOrder?>(null, book.Get(3, 1));
        });
        Run("toggle off leaves latest aim and other followed ships untouched", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1, 2, 3 }, P, 0);
            False(book.ToggleFollow(new long[] { 1, 2 }, Vector3.Zero, 1));
            book.UpdateFollow(new Vector3(10, 0, 20), 2);
            False(book.IsFollowing(1)); False(book.IsFollowing(2)); True(book.IsFollowing(3));
            Order(book.Get(1, 2), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
            Order(book.Get(2, 2), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
            Order(book.Get(3, 2), FleetOrderMode.AimPoint, new Vector3(10, 0, 20), double.PositiveInfinity);
        });
        Run("mixed follow selection enables entire captured group at new point", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0);
            True(book.ToggleFollow(new long[] { 1, 2 }, Vector3.Zero, 1));
            True(book.IsFollowing(1)); True(book.IsFollowing(2));
            Order(book.Get(1, 1), FleetOrderMode.AimPoint, Vector3.Zero, double.PositiveInfinity);
            Order(book.Get(2, 1), FleetOrderMode.AimPoint, Vector3.Zero, double.PositiveInfinity);
        });
        Run("empty or zero follow capture never expands existing set", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0);
            False(book.ToggleFollow(Array.Empty<long>(), Vector3.Zero, 1));
            False(book.ToggleFollow(new long[] { 0, 0 }, Vector3.Zero, 1));
            Equal(1, book.FollowingIds.Count); True(book.IsFollowing(1)); False(book.IsFollowing(0));
            Order(book.Get(1, 1), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
        });
        Run("follow update leaves static aim and current fire unchanged", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0);
            book.Aim(new long[] { 2 }, P, 0); book.Fire(new long[] { 3 }, 10);
            book.UpdateFollow(Vector3.Zero, 11);
            Order(book.Get(2, 11), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
            Order(book.Get(3, 11), FleetOrderMode.FireCurrent, null, 130);
            Equal(3, book.Entries.Count);
        });
        Run("pending follow fire tracks mouse while retaining mode and expiry", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0);
            book.Fire(new long[] { 1 }, 10); var fired = book.Get(1, 10);
            book.UpdateFollow(Vector3.Zero, 11);
            Order(book.Get(1, 11), FleetOrderMode.FirePoint, Vector3.Zero, 130);
            // A previously captured order value stays immutable for consumers such as a launched flight.
            Order(fired, FleetOrderMode.FirePoint, P, 130); True(book.IsFollowing(1));
        });
        Run("completed follow fire resumes latest mouse point", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0);
            book.Fire(new long[] { 1 }, 10); book.UpdateFollow(Vector3.Zero, 11); book.MarkFired(1);
            Order(book.Get(1, 12), FleetOrderMode.AimPoint, Vector3.Zero, double.PositiveInfinity);
            True(book.IsFollowing(1)); book.UpdateFollow(new Vector3(50, 0, 60), 13);
            Order(book.Get(1, 13), FleetOrderMode.AimPoint, new Vector3(50, 0, 60), double.PositiveInfinity);
        });
        Run("expired follow fire resumes latest point at exact 120 seconds", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0);
            book.Fire(new long[] { 1 }, 10); book.UpdateFollow(Vector3.Zero, 129.999);
            Order(book.Get(1, 129.999), FleetOrderMode.FirePoint, Vector3.Zero, 130);
            Order(book.Get(1, 130), FleetOrderMode.AimPoint, Vector3.Zero, double.PositiveInfinity);
            True(book.IsFollowing(1));
        });
        Run("repeat fire replaces deadline once and later follow updates preserve it", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0); book.Fire(new long[] { 1 }, 10);
            book.UpdateFollow(Vector3.Zero, 11); book.Fire(new long[] { 1 }, 20);
            book.UpdateFollow(new Vector3(50, 0, 60), 21);
            Equal(1, book.Entries.Count); Order(book.Get(1, 21), FleetOrderMode.FirePoint, new Vector3(50, 0, 60), 140);
            book.MarkFired(1); Order(book.Get(1, 22), FleetOrderMode.AimPoint, new Vector3(50, 0, 60), double.PositiveInfinity);
        });
        Run("static aim terminates follow only for submitted ships", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1, 2 }, P, 0);
            book.Aim(new long[] { 1 }, Vector3.Zero, 1); book.UpdateFollow(new Vector3(50, 0, 60), 2);
            False(book.IsFollowing(1)); True(book.IsFollowing(2));
            Order(book.Get(1, 2), FleetOrderMode.AimPoint, Vector3.Zero, double.PositiveInfinity);
            Order(book.Get(2, 2), FleetOrderMode.AimPoint, new Vector3(50, 0, 60), double.PositiveInfinity);
        });
        Run("restore removes captured follow and cannot resurrect it on update", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1, 2 }, P, 0);
            book.Fire(new long[] { 1 }, 1); book.Restore(new long[] { 1 }); book.UpdateFollow(Vector3.Zero, 2);
            False(book.IsFollowing(1)); True(book.IsFollowing(2)); Equal<FleetOrder?>(null, book.Get(1, 2));
            Order(book.Get(2, 2), FleetOrderMode.AimPoint, Vector3.Zero, double.PositiveInfinity);
        });
        Run("clear removes follow and all pending orders", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1, 2 }, P, 0); book.Fire(new long[] { 1 }, 1);
            book.Clear(); book.UpdateFollow(Vector3.Zero, 2);
            Equal(0, book.FollowingIds.Count); Equal(0, book.Entries.Count); False(book.IsFollowing(1));
        });
        Run("turning follow off preserves pending point at the moment of toggle", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0); book.Fire(new long[] { 1 }, 10);
            book.UpdateFollow(Vector3.Zero, 11); False(book.ToggleFollow(new long[] { 1 }, Vector3.Zero, 12));
            book.UpdateFollow(new Vector3(50, 0, 60), 13);
            Order(book.Get(1, 13), FleetOrderMode.FirePoint, Vector3.Zero, 130);
            book.MarkFired(1); Order(book.Get(1, 14), FleetOrderMode.AimPoint, Vector3.Zero, double.PositiveInfinity);
        });
        Run("turning follow on replaces previous current fire with aim only", () =>
        {
            var book = new FleetOrderBook(); book.Fire(new long[] { 1 }, 0);
            True(book.ToggleFollow(new long[] { 1 }, P, 1));
            Order(book.Get(1, 2), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
        });
        Run("invalid follow timestamps reject entire mutation", () =>
        {
            foreach (double now in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0);
                ThrowsArgument(() => book.ToggleFollow(new long[] { 1, 2 }, Vector3.Zero, now));
                ThrowsArgument(() => book.UpdateFollow(Vector3.Zero, now));
                Equal(1, book.FollowingIds.Count); True(book.IsFollowing(1)); False(book.IsFollowing(2));
                Order(book.Get(1, 1), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
            }
        });
        Run("invalid follow point components reject entire mutation", () =>
        {
            foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            foreach (var point in new[] { new Vector3(value, 0, 0), new Vector3(0, value, 0), new Vector3(0, 0, value) })
            {
                var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0);
                ThrowsArgument(() => book.ToggleFollow(new long[] { 1, 2 }, point, 1));
                ThrowsArgument(() => book.UpdateFollow(point, 1));
                Equal(1, book.FollowingIds.Count); True(book.IsFollowing(1)); False(book.IsFollowing(2));
                Order(book.Get(1, 1), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
            }
        });
        Run("interrupted capture cannot partially toggle or clear follow", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0);
            Throws<InvalidOperationException>(() => book.ToggleFollow(BrokenIds(), Vector3.Zero, 1));
            Throws<InvalidOperationException>(() => book.Aim(BrokenIds(), Vector3.Zero, 1));
            Throws<InvalidOperationException>(() => book.Restore(BrokenIds()));
            Equal(1, book.FollowingIds.Count); True(book.IsFollowing(1)); False(book.IsFollowing(2));
            Order(book.Get(1, 1), FleetOrderMode.AimPoint, P, double.PositiveInfinity);
        });
        Run("following IDs expose a stable read only snapshot", () =>
        {
            var book = new FleetOrderBook(); book.ToggleFollow(new long[] { 1 }, P, 0);
            var snapshot = book.FollowingIds;
            if (snapshot is ICollection<long> writable) Throws<NotSupportedException>(() => writable.Add(9));
            book.ToggleFollow(new long[] { 2 }, Vector3.Zero, 1); book.Restore(new long[] { 1 });
            Equal(1, snapshot.Count); Equal(1L, snapshot.Single());
            Equal(1, book.FollowingIds.Count); True(book.IsFollowing(2)); False(book.IsFollowing(9));
        });
        Console.WriteLine($"TOTAL {passed + failed}; PASS {passed}; FAIL {failed}");
        return failed == 0 ? 0 : 1;
    }

    private static FleetCandidate Candidate(long id, string? name = null) => new(id, name ?? $"ship-{id}", true, true, true);
    private static IEnumerable<long> BrokenIds() { yield return 1; yield return 2; throw new InvalidOperationException("source interrupted"); }
    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}"); }
    }
    private static void True(bool value) { if (!value) throw new Exception("expected true, got false"); }
    private static void False(bool value) { if (value) throw new Exception("expected false, got true"); }
    private static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, got {actual}"); }
    private static void Order(FleetOrder? order, FleetOrderMode mode, Vector3? point, double expiry)
    {
        True(order != null); Equal(mode, order!.Mode); Equal(point, order.Point); Equal(expiry, order.ExpiresAt);
    }
    private static void ThrowsArgument(Action action) => Throws<ArgumentException>(action);
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"expected {typeof(T).Name}");
    }
}
