using System;
using System.Collections.Generic;
using System.Numerics;

namespace FleetOps.Experimental;

/// <summary>Pure segment versus native box geometry. No target identity, spotting, damage, Unity or IL2CPP APIs.</summary>
internal static class FleetGunImpactGeometry
{
    internal readonly record struct Box(int Id, Matrix4x4 WorldToLocal, Vector3 Center, Vector3 Half);
    internal readonly record struct Hit(int Id, float Fraction, Vector3 Point, Vector3 Normal);

    internal readonly record struct TraceStamp(long Generation, long SourcePart, long Timer, long Arc, long Trajectory);
    internal static bool CanContinueTrace(TraceStamp expected, TraceStamp current, float previousTime, float currentTime,
        float duration, Vector3 previousPosition, Vector3 observedPosition)
    {
        if (expected != current || expected.Generation <= 0 || expected.SourcePart == 0 || expected.Timer == 0
            || expected.Arc == 0 || expected.Trajectory == 0 || !float.IsFinite(previousTime)
            || !float.IsFinite(currentTime) || !float.IsFinite(duration) || previousTime < 0 || currentTime < previousTime
            || currentTime > duration || !Finite(previousPosition) || !Finite(observedPosition)) return false;
        return Vector3.DistanceSquared(previousPosition, observedPosition) < .25f;
    }

    internal static bool TryIntersectSegment(Vector3 start, Vector3 end, Box box, out Hit hit)
    {
        hit = default;
        if (!Finite(start) || !Finite(end) || !Finite(box.Center) || !Finite(box.Half)
            || box.Half.X < 0 || box.Half.Y < 0 || box.Half.Z < 0 || !Finite(box.WorldToLocal)) return false;
        Vector3 delta = end - start;
        if (!Finite(delta) || delta.LengthSquared() <= 1e-12f) return false;
        Matrix4x4 m = box.WorldToLocal;
        if (MathF.Abs(m.M14) > 1e-6f || MathF.Abs(m.M24) > 1e-6f || MathF.Abs(m.M34) > 1e-6f
            || MathF.Abs(m.M44 - 1f) > 1e-6f || !Matrix4x4.Invert(m, out _)) return false;
        Vector3 a = Vector3.Transform(start, m) - box.Center;
        Vector3 b = Vector3.Transform(end, m) - box.Center;
        Vector3 d = b - a;
        if (!Finite(a) || !Finite(b) || !Finite(d)) return false;
        float enter = 0f, exit = 1f;
        Vector3 normal = Vector3.Zero;
        if (!Slab(a.X, d.X, box.Half.X, Vector3.UnitX, ref enter, ref exit, ref normal)
            || !Slab(a.Y, d.Y, box.Half.Y, Vector3.UnitY, ref enter, ref exit, ref normal)
            || !Slab(a.Z, d.Z, box.Half.Z, Vector3.UnitZ, ref enter, ref exit, ref normal)) return false;
        Vector3 point = start + delta * enter;
        // Normal transforms with inverse transpose, preserving nonuniform scale and mirrored axes.
        Vector3 worldNormal = normal == Vector3.Zero ? -Vector3.Normalize(delta)
            : Vector3.TransformNormal(normal, Matrix4x4.Transpose(m));
        if (!Finite(point) || !Finite(worldNormal) || worldNormal.LengthSquared() < 1e-12f) return false;
        worldNormal = Vector3.Normalize(worldNormal);
        hit = new Hit(box.Id, enter, point, worldNormal);
        return true;
    }

    private static bool Slab(float a, float d, float half, Vector3 axis, ref float enter, ref float exit, ref Vector3 normal)
    {
        if (MathF.Abs(d) < 1e-8f) return a >= -half && a <= half;
        float near = (-half - a) / d, far = (half - a) / d;
        Vector3 n = -axis;
        if (near > far) { (near, far) = (far, near); n = axis; }
        if (near > enter) { enter = near; normal = n; }
        exit = MathF.Min(exit, far);
        return enter <= exit && exit >= 0f && enter <= 1f;
    }

    internal static bool TryClosest(Vector3 start, Vector3 end, IReadOnlyList<Box> boxes, out Hit hit)
    {
        hit = default;
        bool found = false;
        for (int i = 0; i < boxes.Count; i++)
            if (TryIntersectSegment(start, end, boxes[i], out Hit candidate)
                && (!found || candidate.Fraction < hit.Fraction)) { hit = candidate; found = true; }
        return found;
    }

    internal static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool Finite(Matrix4x4 m) =>
        float.IsFinite(m.M11) && float.IsFinite(m.M12) && float.IsFinite(m.M13) && float.IsFinite(m.M14)
        && float.IsFinite(m.M21) && float.IsFinite(m.M22) && float.IsFinite(m.M23) && float.IsFinite(m.M24)
        && float.IsFinite(m.M31) && float.IsFinite(m.M32) && float.IsFinite(m.M33) && float.IsFinite(m.M34)
        && float.IsFinite(m.M41) && float.IsFinite(m.M42) && float.IsFinite(m.M43) && float.IsFinite(m.M44);
}
