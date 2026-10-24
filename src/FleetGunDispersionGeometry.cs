using System;
using System.Numerics;

namespace FleetOps.Experimental;

internal static class FleetGunDispersionGeometry
{
    internal readonly record struct Settings(float Close, float Far, float RangeFar);
    // LaunchStart is the shared API name for GetCalcHitPoint's range origin, not the post-factory physical muzzle.
    internal readonly record struct Profile(Vector3 AimCenter, Vector3 ImpactCenter, Vector3 LaunchStart,
        float Range, float Radius, Settings Settings, float CanonicalDistanceRadius = 0f,
        float NativeChance = 0f, float ReferenceRadius = 0f, string CalibrationMethod = "native-distance-prefix");
    internal readonly record struct FrozenShot(Profile Profile, Vector3 ImpactPoint);
    internal const string NativeReferenceCalibration = "native-hit-chance/three-disk-reference-circle";
    private const int CdfResolution = 1024;
    private static readonly Lazy<double[]> CdfTable = new(BuildCdfTable);
    internal static bool TryProfile(Vector3 launch, Vector3 aim, Settings settings, out Profile profile)
    {
        profile = default;
        if (!Finite(launch) || !Finite(aim) || !float.IsFinite(settings.Close) || settings.Close < 0f
            || !float.IsFinite(settings.Far) || settings.Far < settings.Close
            || !float.IsFinite(settings.RangeFar) || settings.RangeFar <= 0f) return false;
        double dx = (double)aim.X-launch.X, dy = (double)aim.Y-launch.Y, dz = (double)aim.Z-launch.Z;
        double distance = Math.Sqrt(dx*dx+dy*dy+dz*dz);
        if (!double.IsFinite(distance) || distance > float.MaxValue) return false;
        float range = (float)distance;
        // Native GetCalcHitPoint's distance Remap has clamp=false: extrapolate beyond RangeFar.
        double radius = settings.Close + ((double)settings.Far-settings.Close)*range/settings.RangeFar;
        if (!double.IsFinite(radius) || radius < 0d || radius > float.MaxValue) return false;
        profile = new Profile(aim, new Vector3(aim.X,0f,aim.Z), launch, range, (float)radius, settings, (float)radius);
        return true;
    }
    // Explicit free-point convention, not native GetCalcHitPoint's target-conditioned mixture.
    // Select R so Pr(|R*(U1+U2+U3)/3| <= referenceRadius) = nativeChance.
    // The native distance-prefix radius is retained as evidence only, never multiplied in.
    internal static bool TryCalibrate(Profile distanceProfile, float nativeChance, float referenceRadius, out Profile profile)
    {
        profile = default;
        if (!TryProfile(distanceProfile.LaunchStart,distanceProfile.AimCenter,distanceProfile.Settings,out Profile canonical)
            || distanceProfile != canonical || !float.IsFinite(nativeChance) || nativeChance <= 0f
            || !float.IsFinite(referenceRadius) || referenceRadius <= 0f) return false;
        float effectiveChance = Math.Min(nativeChance,1f);
        double quantile;
        if (nativeChance >= 1f) quantile = 1d;
        else
        {
            // One deterministic table per process, then O(log N) inversion per moving aim.
            // A continuously changing range/recoil must not reintegrate 40 full CDFs per gun.
            double[] table = CdfTable.Value;
            int lo = 0, hi = CdfResolution;
            while (hi-lo > 1)
            { int middle = (lo+hi)/2; if (table[middle] < effectiveChance) lo = middle; else hi = middle; }
            double delta = table[hi]-table[lo];
            if (!double.IsFinite(delta) || delta <= 0d) return false;
            double mix = Math.Clamp((effectiveChance-table[lo])/delta,0d,1d);
            quantile = (lo+mix)/CdfResolution;
        }
        double radius = referenceRadius/quantile;
        if (!double.IsFinite(radius) || radius < referenceRadius || radius > float.MaxValue) return false;
        profile = canonical with { Radius = (float)radius, NativeChance = effectiveChance,
            ReferenceRadius = referenceRadius, CalibrationMethod = NativeReferenceCalibration };
        return true;
    }

    private static double[] BuildCdfTable()
    {
        var table = new double[CdfResolution+1];
        for (int i = 1; i < CdfResolution; i++)
        {
            double value = ThreeDiskMeanCdf((double)i/CdfResolution);
            if (!double.IsFinite(value)) throw new InvalidOperationException("Native three-disk calibration CDF is non-finite.");
            // The analytic CDF is monotone; retain that invariant against quadrature roundoff.
            table[i] = Math.Max(table[i-1],value);
        }
        table[CdfResolution] = 1d;
        return table;
    }

    // For S=U1+U2 (independent uniform unit disks), vector density is A(1,1,|S|)/pi^2.
    // Condition on |S|=s and intersect the third unit disk with the radius-3r disk:
    // F(r) = integral[0,2] 2*s*A(1,1,s)*A(1,3r,s)/pi^2 ds.
    // Positive Simpson weights preserve monotonicity. This is deterministic numerical
    // quadrature of the native kernel, not RNG, a Gaussian approximation, or an accuracy multiplier.
    internal static double ThreeDiskMeanCdf(double radius)
    {
        if (!double.IsFinite(radius) || radius <= 0d) return 0d;
        if (radius >= 1d) return 1d;
        const int intervals = 512;
        double step = 2d/intervals, sum = 0d;
        for (int i = 0; i <= intervals; i++)
        {
            double s = i*step;
            double value = 2d*s*CircleIntersectionArea(1d,1d,s)*CircleIntersectionArea(1d,3d*radius,s)/(Math.PI*Math.PI);
            sum += (i == 0 || i == intervals ? 1d : i%2 == 0 ? 2d : 4d)*value;
        }
        return Math.Clamp(sum*step/3d,0d,1d);
    }

    private static double CircleIntersectionArea(double a, double b, double distance)
    {
        if (a <= 0d || b <= 0d || distance >= a+b) return 0d;
        if (distance <= Math.Abs(a-b)) { double r = Math.Min(a,b); return Math.PI*r*r; }
        double aa = a*a, bb = b*b, dd = distance*distance;
        double first = Math.Acos(Math.Clamp((dd+aa-bb)/(2d*distance*a),-1d,1d));
        double second = Math.Acos(Math.Clamp((dd+bb-aa)/(2d*distance*b),-1d,1d));
        double radicand = (-distance+a+b)*(distance+a-b)*(distance-a+b)*(distance+a+b);
        return Math.Max(0d,aa*first+bb*second-.5d*Math.Sqrt(Math.Max(0d,radicand)));
    }
    internal static bool TryFreeze(Profile profile, Vector3 nativeDiskAverage, out FrozenShot shot)
    {
        shot = default;
        if (!TryProfile(profile.LaunchStart,profile.AimCenter,profile.Settings,out Profile canonical)) return false;
        if (profile.CalibrationMethod == NativeReferenceCalibration)
        {
            if (!TryCalibrate(canonical,profile.NativeChance,profile.ReferenceRadius,out canonical)) return false;
        }
        if (profile != canonical || !Finite(nativeDiskAverage) || nativeDiskAverage.Y != 0f) return false;
        double x = nativeDiskAverage.X, z = nativeDiskAverage.Z, square = x*x+z*z;
        if (!double.IsFinite(square) || square > 1.000001d) return false;
        // Vector3 float components can round an exact unit-disk boundary a few ulps outward.
        // Bring only that roundoff back to the same support circle; never widen its radius.
        if (square > 1d) { double norm = Math.Sqrt(square); x /= norm; z /= norm; }
        double impactX = canonical.ImpactCenter.X + canonical.Radius*x;
        double impactZ = canonical.ImpactCenter.Z + canonical.Radius*z;
        if (!double.IsFinite(impactX) || !double.IsFinite(impactZ)
            || Math.Abs(impactX) > float.MaxValue || Math.Abs(impactZ) > float.MaxValue) return false;
        shot = new FrozenShot(canonical,new Vector3((float)impactX,0f,(float)impactZ));
        return true;
    }
    private static bool Finite(Vector3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
