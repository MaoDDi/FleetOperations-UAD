using System;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using NVector = System.Numerics.Vector3;
using G = FleetOps.Experimental.FleetGunDispersionGeometry;

namespace FleetOps.Experimental;

/// <summary>Free-point native-precision calibration against an explicit public 20 m diameter circle.
/// The shot kernel is native; the reference-circle probability mapping is an adaptation,
/// not GetCalcHitPoint's target-conditioned hit/miss and rejection distribution.</summary>
internal static class FleetGunDispersion
{
    private static Il2CppSystem.Func<Vector3>? _nativeDisk;
    internal const float ReferenceRadius = 10f;
    // Compatibility with the parent status path. Precision reads no longer mutate native maps.
    internal static bool Faulted => false;
    internal static string FaultReason => "";

    /// <summary>Native GetCalcHitPoint range base, before Shell.Create/verified barrel muzzle offsets.</summary>
    internal static Vector3 RangeOrigin(Part part)
    {
        if (part == null || !part) throw new ArgumentException("A live native Part is required.",nameof(part));
        Transform transform = part.transform;
        Renderer end = part._gunsEnd_k__BackingField;
        Vector3 basis = end != null && end ? end.bounds.m_Center : transform.position;
        return basis + transform.forward*5f;
    }

    /// <summary>Pure preview/profile read: no random number or shot sampler is invoked.</summary>
    internal static bool TryProfile(Vector3 launch, Vector3 aim, out G.Profile profile, out object evidence)
    {
        profile = default;
        try
        {
            var settings = new G.Settings(MonoBehaviourExt.Param("splash_disp_close",30f),
                MonoBehaviourExt.Param("splash_disp_far",150f), MonoBehaviourExt.Param("splash_disp_range_far",10000f));
            bool valid = G.TryProfile(N(launch),N(aim),settings,out profile);
            evidence = new { valid, settingsReadFrom = "running native MonoBehaviourExt.Param",
                settings = new { close = Number(settings.Close), far = Number(settings.Far), rangeFar = Number(settings.RangeFar) },
                profile = valid ? Describe(profile) : null, nativeSamplerCalls = 0,
                error = valid ? null : "Invalid coordinates or dispersion settings: Far >= Close >= 0 and RangeFar > 0 are required.",
                boundary = "Native distance prefix; stationary free point excludes carrier size/angular velocity and target hit/miss selection." };
            return valid;
        }
        catch (Exception ex) { evidence = new { valid = false, nativeSamplerCalls = 0, error = ex.Message }; return false; }
    }

    internal static bool TryProfile(Part part, Vector3 launch, Vector3 aim, out G.Profile profile, out object evidence)
    {
        profile = default;
        try
        {
            if (part == null || !part || part.data?.isGun != true)
            { evidence = new { valid = false, nativeSamplerCalls = 0, error = "Source is not a live native gun Part." }; return false; }
            if (!TryProfile(launch,aim,out var distanceProfile,out evidence)) return false;
            object precision;
            float chance;
            try
            {
                if (!TryNativeChance(part,launch,aim,out chance,out precision))
                    return DistanceFallback(distanceProfile,precision,"Native source design precision is unavailable.",out profile,out evidence);
            }
            catch (Exception ex)
            {
                precision = new { valid = false, method = "native-static-design", nativeSamplerCalls = 0, error = ex.ToString() };
                return DistanceFallback(distanceProfile,precision,"Native source design precision threw before returning a profile.",out profile,out evidence);
            }
            try
            {
                if (!G.TryCalibrate(distanceProfile,chance,ReferenceRadius,out profile))
                    return DistanceFallback(distanceProfile,precision,"Native reference-circle calibration rejected its inputs/result.",out profile,out evidence);
            }
            catch (Exception ex)
            {
                object calibration = new { nativePrecision = precision, error = ex.ToString() };
                return DistanceFallback(distanceProfile,calibration,"Native reference-circle calibration threw.",out profile,out evidence);
            }
            evidence = new { valid = true, precisionAvailable = true, fallback = (string?)null,
                nativeSamplerCalls = 0, precision, profile = Describe(profile), error = (string?)null,
                boundary = "Native static source/design/range accuracy; explicit 20m reference-circle adaptation. Own current speed/turn/recoil and other dynamic aiming penalties are excluded. No hidden target statistics; no distance-radius multiplication." };
            return true;
        }
        catch (Exception ex) { evidence = new { valid = false, nativeSamplerCalls = 0, error = ex.Message }; return false; }
    }

    /// <summary>The only shot-sampling entry. Call once for a matched Shell.Init; freeze its return for the entire flight.</summary>
    internal static bool TrySample(Part part, Vector3 launch, Vector3 aim, out G.FrozenShot shot, out object evidence)
    {
        shot = default;
        if (!TryProfile(part,launch,aim,out G.Profile profile,out evidence)) return false;
        object profileEvidence = evidence;
        int calls = 0;
        try
        {
            // Reuse the exact native stateless lambda. No managed Vector3/Nullable callback trampoline.
            if (_nativeDisk == null)
            {
                _nativeDisk = Ship.__c.__9__701_1;
                if (_nativeDisk == null)
                    _nativeDisk = Il2CppSystem.Delegate.CreateDelegate(Il2CppType.Of<Il2CppSystem.Func<Vector3>>(),
                        Ship.__c.__9, "<GetCalcHitPoint>b__701_1", false, true).Cast<Il2CppSystem.Func<Vector3>>();
                if (_nativeDisk == null) throw new InvalidOperationException("Native disk delegate could not be bound.");
            }
            calls++;
            Vector3 average = Util.DistribNorm(3,_nativeDisk);
            bool valid = G.TryFreeze(profile,N(average),out shot);
            bool precisionAvailable = profile.CalibrationMethod == G.NativeReferenceCalibration;
            evidence = new { valid, precisionAvailable, fallback = precisionAvailable ? null : "native-distance-prefix",
                profile = Describe(profile), precisionProfile = profileEvidence, nativeDiskAverage = V(average),
                impactPoint = valid ? V(shot.ImpactPoint) : null, nativeSamplerCalls = calls,
                nativeDiskDrawsPerSuccessfulSample = 3,
                nativeKernel = "Util.DistribNorm(3, <GetCalcHitPoint>b__701_1): mean of three Random.insideUnitCircle draws mapped to XZ",
                boundary = precisionAvailable
                    ? "One frozen sea endpoint. Radius is the outer support bound; the public 10m-radius reference circle carries nativeChance probability. Neither circle predicts damage to an actual ship."
                    : "Distance dispersion fallback: design precision is unavailable and its actual error is retained. Native distance-prefix support and one native three-disk mean; no 20m probability claim." };
            return valid;
        }
        catch (Exception ex) { evidence = new { valid = false, profile = Describe(profile), precisionProfile = profileEvidence,
            nativeSamplerCalls = calls, error = ex.Message }; return false; }
    }

    private static bool DistanceFallback(G.Profile distanceProfile, object precision, string reason,
        out G.Profile profile, out object evidence)
    {
        // Only a previously validated canonical distance profile can reach this helper.
        // Do not invoke a sampler here: preview and the accepted shot keep the same profile,
        // and TrySample still rejects binding/invalid-kernel failures without another fallback.
        profile = distanceProfile;
        evidence = new { valid = true, precisionAvailable = false, fallback = "native-distance-prefix",
            nativeSamplerCalls = 0, precision, profile = Describe(profile), reason,
            uiLabel = "Distance dispersion (precision unavailable)",
            boundary = "Explicit v007 distance-prefix fallback; native design probability is unavailable. Do not display this as a calibrated 20m reference target or 0-percent precision." };
        return true;
    }

    private static bool TryNativeChance(Part part, Vector3 launch, Vector3 aim, out float chance, out object evidence)
    {
        chance = 0f;
        Ship source = part.ship;
        if (source == null || !source)
        { evidence = new { valid = false, nativeHitChanceCalls = 0, error = "Native source ship is unavailable." }; return false; }
        int calls = 0;
        Ship.ShellType selectedShell = default;
        string shellTypeSource = "unavailable";
        string? readError = null;
        bool fromHasValue = false, targetHasValue = false, shellHasValue = false;
        bool coordinateValuesMatch = false, shellValueMatches = false;
        try
        {
            var fromPoint = new Il2CppSystem.Nullable<Vector3>(launch);
            var targetPoint = new Il2CppSystem.Nullable<Vector3>(aim);
            // Per-PartData ammo can differ from the fleet-wide main/secondary selection.
            // Native Ammo.ShellType is a direct field read (RVA4902C0, +0x18).
            var ammo = source._ammo_k__BackingField;
            if (ammo != null && ammo.ContainsKey(part.data) && ammo[part.data] != null)
            { selectedShell = ammo[part.data].ShellType; shellTypeSource = "current PartData Ammo.ShellType"; }
            else
            { selectedShell = source.IsMainCal(part) ? source.mainShellType : source.secShellType; shellTypeSource = "main/secondary fallback: no current PartData ammo entry"; }
            var shellType = new Il2CppSystem.Nullable<Ship.ShellType>(selectedShell);
            fromHasValue = fromPoint.HasValue;
            targetHasValue = targetPoint.HasValue;
            shellHasValue = shellType.HasValue;
            if (fromHasValue && targetHasValue)
                coordinateValuesMatch = SameVector(fromPoint.Value,launch) && SameVector(targetPoint.Value,aim);
            if (shellHasValue) shellValueMatches = shellType.Value == selectedShell;
            if (!fromHasValue || !targetHasValue || !shellHasValue || !coordinateValuesMatch || !shellValueMatches)
                throw new InvalidOperationException("Native Nullable argument validation failed before HitChance invocation.");
            calls++;
            // Native dynamic=false bypasses glare/smoke at 1F5F910 and the target-dependent
            // dynamic block at 1F6065F. The previous dynamic=true free-point path threw a
            // native NullReferenceException in the user's PID47292 run; never retry it here.
            chance = Ship.HitChance(false,source,fromPoint,null,targetPoint,part,part.data,null,null,shellType);
        }
        catch (Exception ex) { readError = ex.Message; }
        if (readError == null && (!float.IsFinite(chance) || chance <= 0f))
            readError = "Native HitChance returned a non-finite or non-positive probability.";
        bool valid = readError == null && float.IsFinite(chance) && chance > 0f;
        evidence = new { valid, method = "native-static-design", nativeHitChanceCalls = calls, dynamic = false, targetIsNull = true,
            customAimIsNull = true, explicitCoordinates = coordinateValuesMatch,
            nullableArguments = new { fromHasValue, targetHasValue, shellHasValue, coordinateValuesMatch, shellValueMatches },
            nativeChanceRaw = Number(chance), nativeChance = Number(Math.Clamp(chance,0f,1f)),
            crewPreinitializationRequired = false, aimMapWrites = 0, fatalRestoreFailure = false,
            shellType = selectedShell.ToString(), shellTypeSource,
            error = readError,
            source = "Ship.HitChance RVA1F5D770; real source ship, real gun Part and selected native shell type",
            rangefindingWarmupModeled = false,
            included = "Native static design/tech/gun/range/crew training/stability/weather and conning-tower/fire-control damage effects",
            excluded = "Own current speed/turn, acquisition/rangefinding, recoil, flooding/damage instability, glare/smoke and target size/motion/multitarget dynamic effects" };
        return valid;
    }

    private static bool SameVector(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;

    private static object Describe(G.Profile p) => new { aimCenter = V(p.AimCenter), impactCenter = V(p.ImpactCenter),
        launchStart = V(p.LaunchStart), rangeOrigin = V(p.LaunchStart), range = p.Range, radius = p.Radius,
        canonicalDistanceRadius = p.CanonicalDistanceRadius, nativeChance = p.NativeChance,
        referenceRadius = p.ReferenceRadius, referenceDiameter = 2f*p.ReferenceRadius, calibrationMethod = p.CalibrationMethod,
        rangeOriginRole = "GetCalcHitPoint base: gunsEnd.bounds.center (or Part.position) + forward*5; excludes Shell.Create/physical barrel offsets",
        settings = new { close = p.Settings.Close, far = p.Settings.Far, rangeFar = p.Settings.RangeFar },
        formula = p.CalibrationMethod == G.NativeReferenceCalibration
            ? "R = referenceRadius / inverseCDF(nativeChance); native distance prefix retained as evidence only"
            : "close + (far-close)*range/rangeFar; no far-range cap",
        support = "bounded disk; native center-concentrated three-disk mean",
        calibrationNumerics = p.CalibrationMethod == G.NativeReferenceCalibration ? "Once-only 1024-radius CDF table; each point uses 512-interval positive-weight Simpson circle-intersection quadrature. Monotone table inverse by binary search and interpolation; no random draws" : null };
    private static float? Number(float value) => float.IsFinite(value) ? value : null;
    private static NVector N(Vector3 p) => new(p.x,p.y,p.z);
    private static float[]? V(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z)
        ? new[] { p.x,p.y,p.z } : null;
    private static float[] V(NVector p) => new[] { p.X,p.Y,p.Z };
}
