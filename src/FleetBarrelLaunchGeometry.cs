using System;
using System.Collections.Generic;
using System.Numerics;

namespace FleetOps.Experimental;

internal static class FleetBarrelLaunchGeometry
{
    internal static bool TryResolve(IReadOnlyList<Vector3>? pivots, int expectedBarrels, int barrel,
        Vector3 nativeStart, Vector3 lateralAxis, out Vector3 start, out float lateralOffset, out string reason)
    {
        start = nativeStart; lateralOffset = 0f; reason = "INVALID_GEOMETRY";
        if (!Finite(nativeStart)) { reason = "NONFINITE_NATIVE_START"; return false; }
        if (pivots == null || expectedBarrels <= 0 || pivots.Count != expectedBarrels)
        { reason = "PIVOT_COUNT_MISMATCH"; return false; }
        if (barrel < 0 || barrel >= expectedBarrels) { reason = "BARREL_INDEX_OUT_OF_RANGE"; return false; }
        if (expectedBarrels == 1) { reason = "ONE_BARREL_NATIVE_START"; return false; }
        if (!Finite(lateralAxis)) { reason = "NONFINITE_SIDE_AXIS"; return false; }
        double length = Math.Sqrt((double)lateralAxis.X * lateralAxis.X
            + (double)lateralAxis.Y * lateralAxis.Y + (double)lateralAxis.Z * lateralAxis.Z);
        if (length == 0) { reason = "ZERO_SIDE_AXIS"; return false; }
        double ax = lateralAxis.X / length, ay = lateralAxis.Y / length, az = lateralAxis.Z / length;
        double meanX = 0, meanY = 0, meanZ = 0;
        foreach (Vector3 pivot in pivots)
        {
            if (!Finite(pivot)) { reason = "NONFINITE_PIVOT"; return false; }
            meanX += pivot.X; meanY += pivot.Y; meanZ += pivot.Z;
        }
        meanX /= expectedBarrels; meanY /= expectedBarrels; meanZ /= expectedBarrels;
        var offsets = new double[expectedBarrels];
        for (int i = 0; i < expectedBarrels; i++)
        {
            Vector3 pivot = pivots[i];
            offsets[i] = (pivot.X - meanX) * ax + (pivot.Y - meanY) * ay + (pivot.Z - meanZ) * az;
            for (int previous = 0; previous < i; previous++)
                if (offsets[i] == offsets[previous]) { reason = "DUPLICATE_SIDE_PIVOTS"; return false; }
        }
        double chosen = offsets[barrel];
        var resolved = new Vector3((float)(nativeStart.X + ax * chosen),
            (float)(nativeStart.Y + ay * chosen), (float)(nativeStart.Z + az * chosen));
        float offset = (float)chosen;
        if (!Finite(resolved) || !float.IsFinite(offset)) { reason = "OFFSET_OVERFLOW"; return false; }
        start = resolved; lateralOffset = offset; reason = "MEASURED_BARREL_PIVOT_SIDE_OFFSET";
        return true;
    }

    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
