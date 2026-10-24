using System;
using System.Collections.Generic;
using System.Numerics;
using FleetOps.Experimental;

static class BarrelLaunchTests
{
    private static int failed, passed;
    private static readonly Vector3 Origin = new(100, 30, 1000);
    private static readonly Vector3[] Four = { new(-6, 2, 1), new(-2, 4, 3), new(2, 6, 5), new(6, 8, 7) };
    public static int Main()
    {
        Test("four barrels use distinct measured sides and preserve native depth/height", () => {
            var starts = new HashSet<Vector3>(); Vector3 sum = Vector3.Zero;
            for (int barrel = 0; barrel < 4; barrel++)
            {
                True(Resolve(Four, 4, barrel, Origin, Vector3.UnitX, out var start));
                Equal(Origin + new Vector3(Four[barrel].X, 0, 0), start);
                starts.Add(start); sum += start;
            }
            Equal(4, starts.Count); Equal(Origin, sum / 4);
        });
        Test("side axis is normalized and may rotate with the turret", () => {
            True(Resolve(Four, 4, 0, Origin, new(0, 0, 5), out var start));
            Equal(Origin + new Vector3(0, 0, -3), start);
        });
        Test("world translation of pivots does not change side offsets", () => {
            Vector3[] shifted = Array.ConvertAll(Four, p => p + new Vector3(500, 100, -300));
            True(Resolve(shifted, 4, 3, Origin, Vector3.UnitX, out var start)); Equal(Origin + new Vector3(6, 0, 0), start);
        });
        Test("null and zero pivots fall back to native origin", () => {
            Fallback(null, 4, 0); Fallback(Array.Empty<Vector3>(), 0, 0);
        });
        Test("one barrel needs no fabricated offset", () => Fallback(new[] { Vector3.One }, 1, 0));
        Test("negative or past-end barrel index falls back", () => { Fallback(Four, 4, -1); Fallback(Four, 4, 4); });
        Test("mismatched physical pivot count falls back", () => { Fallback(Four, 3, 0); Fallback(Four, 5, 0); });
        Test("malformed zero or duplicate side spacing falls back", () => {
            Fallback(new[] { Vector3.Zero, Vector3.Zero }, 2, 0);
            Fallback(new[] { Vector3.Zero, Vector3.UnitY }, 2, 0);
        });
        Test("nonfinite pivot does not move native origin", () => {
            Vector3[] bad = (Vector3[])Four.Clone(); bad[2] = new(float.NaN, 0, 0); Fallback(bad, 4, 0);
            bad[2] = new(0, float.PositiveInfinity, 0); Fallback(bad, 4, 0);
        });
        Test("zero or nonfinite side axis falls back", () => {
            False(Resolve(Four, 4, 0, Origin, Vector3.Zero, out var start)); Equal(Origin, start);
            False(Resolve(Four, 4, 0, Origin, new(float.NaN, 0, 0), out start)); Equal(Origin, start);
        });
        Test("nonfinite native origin is never replaced with invented finite data", () => {
            False(Resolve(Four, 4, 0, new(float.NaN, 0, 0), Vector3.UnitX, out var start)); True(float.IsNaN(start.X));
        });
        Test("overflow in adjusted native origin falls back", () => {
            var huge = new[] { new Vector3(-float.MaxValue, 0, 0), new Vector3(float.MaxValue, 0, 0) };
            var native = new Vector3(float.MaxValue, 0, 0);
            False(Resolve(huge, 2, 1, native, Vector3.UnitX, out var start)); Equal(native, start);
        });
        Console.WriteLine($"TOTAL {passed + failed}; PASSED {passed}; FAILED {failed}"); return failed == 0 ? 0 : 1;
    }
    private static bool Resolve(IReadOnlyList<Vector3>? p, int expected, int barrel, Vector3 origin, Vector3 axis, out Vector3 start)
        => FleetBarrelLaunchGeometry.TryResolve(p, expected, barrel, origin, axis, out start, out _, out _);
    private static void Fallback(IReadOnlyList<Vector3>? p, int expected, int barrel)
    { False(Resolve(p, expected, barrel, Origin, Vector3.UnitX, out var start)); Equal(Origin, start); }
    private static void Test(string name, Action action)
    { try { action(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
    private static void True(bool value) { if (!value) throw new Exception("expected true"); }
    private static void False(bool value) { if (value) throw new Exception("expected false"); }
    private static void Equal<T>(T expected, T actual) where T : notnull
    { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"expected {expected}, actual {actual}"); }
}
