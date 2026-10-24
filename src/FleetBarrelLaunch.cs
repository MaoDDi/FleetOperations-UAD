using System;
using Il2Cpp;
using UnityEngine;
using NVector = System.Numerics.Vector3;

namespace FleetOps.Experimental;

/// <summary>Measures each native barrel pivot's transverse spacing. This is not an exact muzzle-tip solver.
/// Call once before native Shell.Init calculates its arc; the caller applies a successful start once.
/// This helper never changes a projectile, creates ammunition, adds random scatter or writes native state.</summary>
public static class FleetBarrelLaunch
{
    public static bool TryResolve(Part from, int barrel, Vector3 nativeStart, out Vector3 start, out object evidence)
    {
        start = nativeStart;
        evidence = new { resolved = false, reason = "UNAVAILABLE_NATIVE_PART", barrel };
        try
        {
            if (from is null || from.data?.isGun != true) return false;
            int expected = from.data.barrels;
            var barrels = from._gunBarrels_k__BackingField;
            int observed = barrels?.Count ?? 0;
            if (expected <= 1 || expected != observed || barrel < 0 || barrel >= expected)
            {
                evidence = new { resolved = false, reason = expected == 1 ? "ONE_BARREL_NATIVE_START" : "BARREL_MAPPING_UNAVAILABLE",
                    part = from.Pointer.ToInt64(), barrel, expected, observed, nativeStart = V(nativeStart) };
                return false;
            }
            var pivots = new NVector[observed];
            for (int i = 0; i < observed; i++)
            {
                GameObject pivot = barrels![i];
                if (pivot == null) throw new InvalidOperationException("Native barrel pivot is unavailable.");
                pivots[i] = N(pivot.transform.position);
            }
            Vector3 side = from.transform.right;
            bool resolved = FleetBarrelLaunchGeometry.TryResolve(pivots, expected, barrel, N(nativeStart), N(side),
                out NVector result, out float offset, out string reason);
            if (resolved) start = new Vector3(result.X, result.Y, result.Z);
            evidence = new { resolved, reason, part = from.Pointer.ToInt64(), barrel, expected, observed,
                nativeStart = V(nativeStart), start = V(start), lateralAxis = V(side), lateralOffset = offset,
                selectedPivotName = barrels![barrel].name,
                selectedPivot = V(new Vector3(pivots[barrel].X, pivots[barrel].Y, pivots[barrel].Z)),
                boundary = "Measured pivot side spacing only; native longitudinal launch depth retained. No exact muzzle-tip claim or added scatter." };
            return resolved;
        }
        catch (Exception ex)
        {
            start = nativeStart;
            evidence = new { resolved = false, reason = "BARREL_GEOMETRY_READ_ERROR", barrel,
                nativeStart = V(nativeStart), error = ex.Message };
            return false;
        }
    }

    private static NVector N(Vector3 v) => new(v.x, v.y, v.z);
    private static float[]? V(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z)
        ? new[] { v.x, v.y, v.z } : null;
}
