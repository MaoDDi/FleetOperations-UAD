using System;
using System.Numerics;

namespace FleetOps.Experimental;

/// <summary>Instantaneous-launch, constant-target-velocity estimates. No RNG or native state.</summary>
internal static class FleetFireSolutionGeometry
{
    internal static bool TryIntercept(Vector3 origin, Vector3 target, Vector3 targetVelocity,
        float speed, float range, out float seconds, out Vector3 point)
    {
        seconds = 0; point = default;
        if (!Finite(origin) || !Finite(target) || !Finite(targetVelocity)
            || !float.IsFinite(speed) || speed <= 0 || !float.IsFinite(range) || range <= 0) return false;
        double x = (double)target.X-origin.X, z = (double)target.Z-origin.Z;
        double vx = targetVelocity.X, vz = targetVelocity.Z;
        double a = vx*vx+vz*vz-(double)speed*speed, b = 2*(x*vx+z*vz), c = x*x+z*z;
        if (c < .000001) return false;
        double t;
        if (Math.Abs(a) < 1e-10*Math.Max(1,(double)speed*speed))
        { if (Math.Abs(b) < 1e-12) return false; t = -c/b; }
        else
        {
            double discriminant = b*b-4*a*c;
            if (!double.IsFinite(discriminant) || discriminant < 0) return false;
            double q = -.5*(b + (b >= 0 ? 1 : -1)*Math.Sqrt(discriminant));
            double t1 = q/a, t2 = q == 0 ? double.NaN : c/q;
            t = t1 > 0 && double.IsFinite(t1) ? t1 : double.PositiveInfinity;
            if (t2 > 0 && double.IsFinite(t2)) t = Math.Min(t,t2);
        }
        if (!double.IsFinite(t) || t <= 0 || t*speed > range || t > float.MaxValue) return false;
        seconds = (float)t;
        return TryAdvance(target,targetVelocity,seconds,out point);
    }

    internal static bool TryAdvance(Vector3 position, Vector3 velocity, float seconds, out Vector3 point)
    {
        point = default;
        if (!Finite(position) || !Finite(velocity) || !float.IsFinite(seconds) || seconds < 0) return false;
        var candidate = new Vector3((float)(position.X+(double)velocity.X*seconds), 0,
            (float)(position.Z+(double)velocity.Z*seconds));
        if (!Finite(candidate)) return false;
        point = candidate; return true;
    }

    internal static bool TryTube(Vector3 origin, Vector3 aim, Vector3 partRight, int tubes, int index,
        out Vector3 launch, out Vector3 direction, out float coefficient)
    {
        launch = direction = default; coefficient = 0;
        if (!Finite(origin) || !Finite(aim) || !Finite(partRight) || tubes < 1 || tubes > 64 || index < 0 || index >= tubes) return false;
        Vector3 d = aim-origin; d.Y = 0;
        if (d.LengthSquared() < .000001f || !float.IsFinite(d.LengthSquared())) return false;
        d = Vector3.Normalize(d);
        // Exact native branch order, including the central first tube and asymmetric pair.
        coefficient = index == 0 ? 0 : tubes == 2 ? -1 : 2f*(index-1)/(tubes-2)-1;
        var right = new Vector3(partRight.X,0,partRight.Z);
        launch = origin+right*(3f*coefficient); launch.Y = 0;
        direction = Vector3.Normalize(d + new Vector3(d.Z,0,-d.X)*(.05f*coefficient));
        return Finite(launch) && Finite(direction);
    }

    internal static bool TryRandomHalfAngle(float nativeInaccuracy, float nativeTechA, float crew,
        float crewPenalty, out float radians, out float diskRadius)
    {
        radians = diskRadius = 0;
        if (!float.IsFinite(nativeInaccuracy) || !float.IsFinite(nativeTechA) || nativeTechA <= 0
            || !float.IsFinite(crew) || !float.IsFinite(crewPenalty)) return false;
        float crewScale = crew < 1 ? 1-(1-crew)*crewPenalty : 1;
        diskRadius = Math.Abs(nativeInaccuracy/nativeTechA*crewScale);
        // A disk reaching the normalized-vector origin no longer has a narrow fan bound.
        if (!float.IsFinite(diskRadius) || diskRadius >= 1) return false;
        radians = (float)Math.Asin(diskRadius);
        return float.IsFinite(radians);
    }

    internal static Vector3 RotateY(Vector3 unit, float radians) => new(
        (float)(unit.X*Math.Cos(radians)+unit.Z*Math.Sin(radians)),0,
        (float)(unit.Z*Math.Cos(radians)-unit.X*Math.Sin(radians)));
    internal static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
}
