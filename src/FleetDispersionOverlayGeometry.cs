using System;
using System.Numerics;

namespace FleetOps.Experimental;

internal static class FleetDispersionOverlayGeometry
{
    internal static bool TryEllipsePoint(Vector3 center, Vector3 along, Vector3 across,
        float alongRadius, float acrossRadius, double angle, out Vector3 point)
    {
        point = default;
        if (!Finite(center) || !Finite(along) || !Finite(across) || !double.IsFinite(angle)
            || !float.IsFinite(alongRadius) || !float.IsFinite(acrossRadius)
            || alongRadius < 0 || acrossRadius < 0 || Math.Max(alongRadius, acrossRadius) <= 0
            || Math.Abs(along.LengthSquared() - 1) > .01f || Math.Abs(across.LengthSquared() - 1) > .01f
            || Math.Abs(along.Y) > .001f || Math.Abs(across.Y) > .001f
            || Math.Abs(Vector3.Dot(along, across)) > .01f) return false;
        double u = alongRadius * Math.Cos(angle), v = acrossRadius * Math.Sin(angle);
        var candidate = new Vector3((float)(center.X + along.X * u + across.X * v),
            (float)(center.Y + along.Y * u + across.Y * v),
            (float)(center.Z + along.Z * u + across.Z * v));
        if (!Finite(candidate)) return false;
        point = candidate;
        return true;
    }

    internal static bool TryScreenSegment(Vector3 projectedA, Vector3 projectedB, float near,
        float screenHeight, Vector4 pixelRect, out Vector2 a, out Vector2 b)
    {
        a = b = default;
        // Conservative near-plane clipping: omit a crossing segment, never connect
        // front points across a hidden/back-facing arc or project through z=0.
        if (!Finite(projectedA) || !Finite(projectedB) || !float.IsFinite(near) || near <= 0
            || projectedA.Z <= near || projectedB.Z <= near
            || !float.IsFinite(screenHeight) || screenHeight <= 0 || !Finite(pixelRect)
            || pixelRect.Z <= 0 || pixelRect.W <= 0) return false;
        double left = Math.Max(0, pixelRect.X), right = (double)pixelRect.X + pixelRect.Z;
        double top = Math.Max(0, screenHeight - ((double)pixelRect.Y + pixelRect.W));
        double bottom = Math.Min(screenHeight, screenHeight - (double)pixelRect.Y);
        if (right <= left || bottom <= top) return false;
        double ax = projectedA.X, ay = screenHeight - (double)projectedA.Y;
        double dx = (double)projectedB.X - ax, dy = projectedA.Y - (double)projectedB.Y;
        double diagonalSquared = (right-left)*(right-left)+(bottom-top)*(bottom-top);
        if (dx*dx+dy*dy > 64*diagonalSquared) return false;
        double enter = 0, exit = 1;
        if (!Clip(-dx, ax-left, ref enter, ref exit) || !Clip(dx, right-ax, ref enter, ref exit)
            || !Clip(-dy, ay-top, ref enter, ref exit) || !Clip(dy, bottom-ay, ref enter, ref exit)) return false;
        a = new Vector2((float)(ax + enter*dx), (float)(ay + enter*dy));
        b = new Vector2((float)(ax + exit*dx), (float)(ay + exit*dy));
        return Finite(a) && Finite(b) && a != b;
    }

    private static bool Clip(double p, double q, ref double enter, ref double exit)
    {
        if (p == 0) return q >= 0;
        double t = q / p;
        if (p < 0) { if (t > exit) return false; enter = Math.Max(enter, t); }
        else { if (t < enter) return false; exit = Math.Min(exit, t); }
        return true;
    }
    private static bool Finite(Vector2 v) => float.IsFinite(v.X) && float.IsFinite(v.Y);
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Vector4 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z) && float.IsFinite(v.W);
}
