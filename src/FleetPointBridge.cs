using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Il2Cpp;
using Il2CppInterop.Runtime;
using UnityEngine;
using UObject = UnityEngine.Object;
using AimMap = Il2CppSystem.Collections.Generic.Dictionary<Il2Cpp.PartData, Il2Cpp.Ship.Aim>;
using ReceivedMap = Il2CppSystem.Collections.Generic.Dictionary<Il2Cpp.PartData, Il2Cpp.Part.WeaponStatistics>;

namespace FleetOps.Experimental;

/// <summary>Disposable point-order experiment. No ammo, reload, statistics or damage writes.</summary>
public static class FleetPointBridge
{
    public static bool Faulted => _faulted;
    private sealed class PointSource
    {
        internal Ship Source = null!, Anchor = null!;
        internal readonly AimMap Right = new(), Left = new();
        internal readonly Part.WeaponStatistics ReceivedSink = new();
        internal readonly ReceivedMap ReceivedByTypeSink = new();
    }

    private sealed class Scope : IDisposable
    {
        internal readonly Scope? Previous;
        internal readonly Ship Source;
        internal readonly Vector3 Point;
        internal readonly bool AllowShot, TorpedoAim;
        internal readonly HashSet<IntPtr> Transforms = new();
        internal readonly Dictionary<IntPtr, Ship> Anchors = new();
        internal readonly Dictionary<IntPtr, CountdownTimer> ReloadTimers = new();
        internal readonly HashSet<IntPtr> OtherReloadTimers = new();
        internal Vector3 OriginalAnchorVelocity;
        internal bool VelocitySwapped;
        internal PointSource Private = null!;
        internal AimMap? OriginalRight, OriginalLeft;
        internal Part.WeaponStatistics? OriginalReceived;
        internal ReceivedMap? OriginalReceivedByType;
        internal bool RightSwapped, LeftSwapped, ReceivedSwapped, ReceivedByTypeSwapped;
        internal bool Verified, Disposed;
        internal Scope(Ship source, Vector3 point, bool allowShot, bool torpedoAim)
        { Previous = _scope; Source = source; Point = point; AllowShot = allowShot; TorpedoAim = torpedoAim; }
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            bool restored = true;
            void Restore(string what, Action action)
            { try { action(); } catch (Exception ex) { restored = false; _faulted = true; Error("restore_" + what, ex.ToString()); } }
            // Restore the exact original velocity and references, including native exception exits.
            if (VelocitySwapped) Restore("anchor_velocity", () => {
                Private.Anchor.velocity = OriginalAnchorVelocity;
                Vector3 observed = Private.Anchor.velocity;
                if (observed.x != OriginalAnchorVelocity.x || observed.y != OriginalAnchorVelocity.y || observed.z != OriginalAnchorVelocity.z)
                    throw new InvalidOperationException("Anchor velocity restoration mismatch.");
                Interlocked.Increment(ref _torpedoVelocityRestores);
            });
            if (RightSwapped) Restore("right_aims", () => Source._enemies_k__BackingField = OriginalRight!);
            if (LeftSwapped) Restore("left_aims", () => Source._enemiesLeftSide_k__BackingField = OriginalLeft!);
            if (ReceivedSwapped) Restore("anchor_received", () => Private.Anchor._statisticsReceived_k__BackingField = OriginalReceived!);
            if (ReceivedByTypeSwapped) Restore("anchor_received_by_type", () => Private.Anchor._statisticsReceivedWeaponType_k__BackingField = OriginalReceivedByType!);
            if (restored && RightSwapped && LeftSwapped) Interlocked.Increment(ref _privateAimRestores);
            if (ReferenceEquals(_scope, this)) _scope = Previous;
            else if (RightSwapped || LeftSwapped) { _scope = null; _faulted = true; Error("scope_order", "Point scopes must dispose in LIFO order."); }
        }
    }

    private sealed class CompletedGunGate : IDisposable
    {
        internal readonly CompletedGunGate? Previous;
        internal readonly HashSet<IntPtr> Timers;
        internal bool Disposed;
        internal CompletedGunGate(IEnumerable<IntPtr> timers)
        {
            Previous = _completedGunGate;
            Timers = new HashSet<IntPtr>(timers);
            Timers.Remove(IntPtr.Zero);
        }
        public void Dispose()
        {
            if (Disposed) return;
            Disposed = true;
            if (ReferenceEquals(_completedGunGate, this)) _completedGunGate = Previous;
            else { _completedGunGate = null; _faulted = true; Error("completed_gun_gate_order", "Gun gate leases must dispose in LIFO order."); }
        }
    }

    private sealed class Flight
    {
        internal IntPtr Pointer;
        internal long ImpactGeneration;
        internal bool GeometryImpact, NativeDamageReturned;
        internal string ImpactVictim = "";
        internal Shell Shell = null!;
        internal Ship Source = null!;
        internal Ship Anchor = null!;
        internal Vector3 AimCenter, Point;
        internal object? DispersionEvidence;
        internal FleetGunDispersionGeometry.Profile FrozenDispersion;
        // Keep both native closure objects and the native delegate rooted through the whole flight.
        internal Ship.__c__DisplayClass701_0 TargetOwner = null!;
        internal Ship.__c__DisplayClass701_1 CallbackOwner = null!;
        internal Il2CppSystem.Func<Il2CppSystem.Nullable<Vector3>> Callback = null!;
        // Evidence only: no value below is used to control the shell or its lifetime.
        internal string SourceId = "", AnchorId = "";
        internal readonly DateTime EvidenceCreatedUtc = DateTime.UtcNow;
        internal DateTime LastObservedUtc;
        internal Vector3 LastPosition, LastAnchorPosition, LastNativeTarget;
        internal float LastHorizontal, LastSeaVertical, LastPointVertical, LastPointDistance, LastElapsed, LastDuration, LastProgress;
        internal float MinHorizontal = float.PositiveInfinity, MinSeaVertical = float.PositiveInfinity;
        internal float MinPointDistance = float.PositiveInfinity;
        internal float MinHorizontalNearSea = float.PositiveInfinity;
        internal int Samples, Observations, LastFrame;
        internal bool EndEmitted, ObservationErrorEmitted, LastAnchorAvailable, LastNativeTargetAvailable, LastTimingAvailable;
    }

    private sealed class TorpedoFlight
    {
        internal Torpedo Torpedo = null!;
        internal Vector3 Point, LastPosition, Direction;
        internal string SourceId = "";
        internal int Samples;
        internal float LastElapsed, LastDuration;
        internal bool ErrorEmitted;
    }

    [ThreadStatic] private static Scope? _scope;
    [ThreadStatic] private static int _suspended;
    [ThreadStatic] private static CompletedGunGate? _completedGunGate;
    private static readonly Dictionary<IntPtr, Flight> Flights = new();
    private static readonly Dictionary<(IntPtr, bool), PointSource> PointSources = new();
    private static readonly Dictionary<IntPtr, TorpedoFlight> TorpedoFlights = new();
    private static Action<string, object>? _emit;
    private static bool _installed, _faulted;
    private static int _mainThread;
    private static long _enters, _getterHits, _reloadGateHits, _initAttempts, _accepted, _blocked, _errors, _completed, _refreshes;
    private static string _lastError = "", _getterAbi = "NOT_RUN", _reloadGateAbi = "NOT_RUN", _callbackAbi = "NOT_RUN";
    private static long _trajectorySamples, _trajectoryEnds, _trajectoryReadErrors;
    private static long _privateAimRestores, _pointEligibilityChecks, _pointSpottingSuspends, _internalAnchorSelections;
    private static long _completedGunGateHits, _barrelLaunchResolved, _barrelLaunchFallbacks;
    private static long _torpedoEnters, _torpedoVelocityOverrides, _torpedoVelocityRestores, _torpedoCreates, _torpedoSamples, _torpedoEnds, _torpedoReadErrors;
    private static readonly float[] SampleProgress = { 0f, .15f, .30f, .45f, .60f, .75f, .90f, .99f };

    /// <summary>Install only simple-signature hooks. GetCalcHitPoint and Shell.Create are never patched.</summary>
    public static void Install(HarmonyLib.Harmony harmony, Action<string, object> emit)
    {
        if (_installed) return;
        _emit = emit ?? throw new ArgumentNullException(nameof(emit));
        FleetGunImpactBridge.Initialize(Emit);
        _mainThread = Environment.CurrentManagedThreadId;
        var added = new List<(MethodBase Target, MethodInfo Patch)>();
        void Patch(MethodBase target, string? prefix = null, string? finalizer = null)
        {
            MethodInfo? p = prefix == null ? null : AccessTools.Method(typeof(FleetPointBridge), prefix);
            MethodInfo? f = finalizer == null ? null : AccessTools.Method(typeof(FleetPointBridge), finalizer);
            // Record before patching so a partially successful backend installation is also unwound.
            if (p != null) added.Add((target, p));
            if (f != null) added.Add((target, f));
            harmony.Patch(target, prefix: p == null ? null : new HarmonyMethod(p),
                finalizer: f == null ? null : new HarmonyMethod(f));
        }
        try
        {
            Patch(AccessTools.PropertyGetter(typeof(Transform), nameof(Transform.position))
                ?? throw new MissingMethodException("Transform.get_position"), nameof(PositionPrefix));
            Patch(AccessTools.PropertyGetter(typeof(CountdownTimer), nameof(CountdownTimer.isRunning))
                ?? throw new MissingMethodException("CountdownTimer.get_isRunning"), nameof(ReloadGatePrefix));
            Patch(AccessTools.Method(typeof(Shell), nameof(Shell.Init), Type.EmptyTypes)
                ?? throw new MissingMethodException("Shell.Init"), nameof(InitPrefix), nameof(InitFinalizer));
            Patch(AccessTools.Method(typeof(Shell), nameof(Shell.Update), Type.EmptyTypes)
                ?? throw new MissingMethodException("Shell.Update"), nameof(UpdatePrefix), nameof(UpdateFinalizer));
            Patch(AccessTools.Method(typeof(Ship), nameof(Ship.CheckSpotting), Type.EmptyTypes)
                ?? throw new MissingMethodException("Ship.CheckSpotting"), nameof(SuspendPrefix), nameof(SuspendFinalizer));
            Patch(AccessTools.Method(typeof(Ship), nameof(Ship.IsEnemyFine), new[] { typeof(Ship), typeof(Part), typeof(bool) })
                ?? throw new MissingMethodException("Ship.IsEnemyFine"), nameof(PointEligibilityPrefix));
            Patch(AccessTools.Method(typeof(Torpedo), nameof(Torpedo.Create), new[] { typeof(Part), typeof(Vector3), typeof(Vector3), typeof(float), typeof(float) })
                ?? throw new MissingMethodException("Torpedo.Create"), nameof(TorpedoCreatePrefix), nameof(TorpedoCreateFinalizer));
            _installed = true;
            Emit("POINT_BRIDGE_INSTALLED", new { runtime = "NOT_RUN", nativeCallback = "existing native closure; no managed Nullable return delegate", defaultActive = false });
        }
        catch (Exception ex)
        {
            _faulted = true;
            foreach (var patch in added)
                try { harmony.Unpatch(patch.Target, patch.Patch); } catch { /* report original installation failure */ }
            Error("install", ex.ToString());
            throw;
        }
    }

    /// <summary>Caller must hold this lease only around this source's single native AimAndShoot invocation.
    /// A failed Enter throws: the caller must suppress that point order, never fall back to enemy fire.</summary>
    public static IDisposable Enter(Ship source, Vector3 point, bool allowShot) => Enter(source, point, allowShot, false);

    /// <summary>torpedoAim selects a separate point and native torpedo branch. Caller must set both gun modes Off
    /// for this invocation; gun scopes must set torpedoMode Off. No lease may survive the synchronous native call.</summary>
    public static IDisposable Enter(Ship source, Vector3 point, bool allowShot, bool torpedoAim)
    {
        RequireThread();
        if (!_installed || _faulted) throw new InvalidOperationException("Point bridge unavailable or faulted.");
        if (source == null || source.state != Ship.State.Alive || !Finite(point))
            throw new ArgumentException("Point order requires a live source and finite world point.");
        if (torpedoAim && (source.gunMode != Ship.ShootMode.Off || source.gunSecMode != Ship.ShootMode.Off))
            throw new InvalidOperationException("Torpedo point scope requires both gun modes Off for this native invocation.");
        if (!torpedoAim && source.torpedoMode != Ship.ShootMode.Off)
            throw new InvalidOperationException("Gun point scope requires torpedoMode Off for this native invocation.");
        if (torpedoAim)
        {
            Vector3 delta = point - source.transform.position;
            if (delta.x * delta.x + delta.z * delta.z < 1f)
                throw new ArgumentException("Torpedo point must be at least one metre from the source.");
        }
        var scope = new Scope(source, point, allowShot, torpedoAim);
        int suspended = _suspended;
        _suspended++;
        try
        {
            scope.Private = GetPrivatePointSource(source, torpedoAim);
            Ship target = scope.Private.Anchor;
            scope.Anchors[target.Pointer] = target;
            scope.Transforms.Add(target.transform.Pointer);
            scope.Transforms.Add(target.generalCont.transform.Pointer);
            if (source.parts != null)
                foreach (Part part in source.parts)
                    if (part?.data != null && (part.data.isGun || part.data.isTorpedo))
                    {
                        bool selected = torpedoAim ? part.data.isTorpedo : part.data.isGun;
                        if (!selected)
                        {
                            if (part._reloading_k__BackingField != null) scope.OtherReloadTimers.Add(part._reloading_k__BackingField.Pointer);
                            continue;
                        }
                        SeedPrivateAim(scope.Private.Right, part.data, target);
                        SeedPrivateAim(scope.Private.Left, part.data, target);
                        if (part._reloading_k__BackingField != null)
                            scope.ReloadTimers[part._reloading_k__BackingField.Pointer] = part._reloading_k__BackingField;
                    }
            scope.Transforms.Remove(source.transform.Pointer);
            if (source.generalCont != null) scope.Transforms.Remove(source.generalCont.transform.Pointer);
            if (scope.Transforms.Count == 0 || scope.Private.Right.Count == 0)
                throw new InvalidOperationException("No usable selected weapon or private point calculation anchor exists.");
        }
        finally { _suspended = suspended; }
        _scope = scope;
        try
        {
            if (torpedoAim)
            {
                // AimAndShoot reads Ship.velocity directly at 0x1F1E801/813 before native intercept/fan/scatter.
                // A stationary sea point has zero target velocity. This synchronous field override never moves
                // the real anchor; it is restored before the source invocation returns, including exceptions.
                scope.OriginalAnchorVelocity = scope.Private.Anchor.velocity;
                if (!Finite(scope.OriginalAnchorVelocity)) throw new InvalidOperationException("Nonfinite internal anchor velocity.");
                scope.VelocitySwapped = true;
                scope.Private.Anchor.velocity = Vector3.zero;
                if (scope.Private.Anchor.velocity.sqrMagnitude != 0f) throw new InvalidOperationException("Stationary point velocity verification failed.");
                Interlocked.Increment(ref _torpedoVelocityOverrides);
            }
            scope.OriginalRight = source._enemies_k__BackingField;
            scope.OriginalLeft = source._enemiesLeftSide_k__BackingField;
            source._enemies_k__BackingField = scope.Private.Right; scope.RightSwapped = true;
            source._enemiesLeftSide_k__BackingField = scope.Private.Left; scope.LeftSwapped = true;
            // RegisterMadeShot must still update real source/native UI shot counters. Its two receiver
            // writes go to private sinks, so a hidden calculation anchor receives no fictional attack stats.
            scope.OriginalReceived = scope.Private.Anchor._statisticsReceived_k__BackingField;
            scope.OriginalReceivedByType = scope.Private.Anchor._statisticsReceivedWeaponType_k__BackingField;
            scope.Private.Anchor._statisticsReceived_k__BackingField = scope.Private.ReceivedSink; scope.ReceivedSwapped = true;
            scope.Private.Anchor._statisticsReceivedWeaponType_k__BackingField = scope.Private.ReceivedByTypeSink; scope.ReceivedByTypeSwapped = true;
            // Exercise the installed native->managed blittable Vector3 return path before permitting shots.
            Ship anchor = First(scope.Anchors);
            long before = Interlocked.Read(ref _getterHits);
            Vector3 observed = anchor.transform.position;
            if (Interlocked.Read(ref _getterHits) == before || !Near(observed, point))
                throw new InvalidOperationException("Scoped native Transform.position getter verification failed.");
            scope.Verified = true;
            _getterAbi = "OBSERVED_RETURN";
            if (!allowShot)
            {
                if (scope.ReloadTimers.Count == 0) throw new InvalidOperationException("No selected weapon reload timer exists for the read-only hold-fire gate.");
                CountdownTimer? timer = null;
                foreach (CountdownTimer value in scope.ReloadTimers.Values) { timer = value; break; }
                long gateBefore = Interlocked.Read(ref _reloadGateHits);
                if (timer == null || !timer.isRunning || Interlocked.Read(ref _reloadGateHits) == gateBefore)
                    throw new InvalidOperationException("Native reload bool read gate verification failed.");
                _reloadGateAbi = "OBSERVED_RETURN";
            }
            Interlocked.Increment(ref _enters);
            if (torpedoAim) Interlocked.Increment(ref _torpedoEnters);
            return scope;
        }
        catch (Exception ex)
        {
            scope.Dispose(); _faulted = true;
            if (_getterAbi != "OBSERVED_RETURN") _getterAbi = "FAIL";
            else if (!allowShot) _reloadGateAbi = "FAIL";
            Error("enter_verification", ex.ToString()); throw;
        }
    }

    /// <summary>Holds only the supplied completed gun-turret reload reads during one synchronous AimAndShoot.
    /// Caller supplies gun timers only and disposes before a separate torpedo invocation. No timer is mutated.</summary>
    public static IDisposable EnterCompletedGunGate(IEnumerable<IntPtr> reloadTimers)
    {
        RequireThread();
        if (!_installed || _faulted) throw new InvalidOperationException("Gun read gate unavailable or faulted.");
        if (reloadTimers == null) throw new ArgumentNullException(nameof(reloadTimers));
        var lease = new CompletedGunGate(reloadTimers);
        _completedGunGate = lease;
        return lease;
    }

    private static PointSource GetPrivatePointSource(Ship source, bool torpedoAim)
    {
        if (PointSources.TryGetValue((source.Pointer, torpedoAim), out PointSource? state)
            && state.Source != null && state.Source.Pointer == source.Pointer && UsableAnchor(source, state.Anchor)) return state;
        Ship? anchor = null;
        // Do not use visibility, target assignments, distance, names, or discovered contacts to choose.
        // The existing object is only an internal parameter carrier; no Ship/model is spawned or moved.
        if (Ship.AllShips != null)
            foreach (Ship candidate in Ship.AllShips)
                if (UsableAnchor(source, candidate)) { anchor = candidate; break; }
        if (anchor == null) throw new InvalidOperationException("No alive internal battle opponent exists; point order fails closed.");
        state = new PointSource { Source = source, Anchor = anchor };
        PointSources[(source.Pointer, torpedoAim)] = state;
        Interlocked.Increment(ref _internalAnchorSelections);
        Emit("POINT_INTERNAL_ANCHOR", new { source = source.id.ToString(), anchor = anchor.id.ToString(),
            visibleAtSelection = anchor.IsVisibleFor(source), shownAtSelection = anchor._isShownForOthers_k__BackingField,
            revealedUntilAtSelection = anchor.RevealedUntil, unityTime = Time.time,
            selection = "internal alive opponent; selection does not depend on visibility; no name/position exposed to UI" });
        return state;
    }

    private static bool UsableAnchor(Ship source, Ship? candidate) => candidate != null && candidate
        && candidate.Pointer != source.Pointer && candidate.state == Ship.State.Alive
        && !candidate.IsSurrendered
        && candidate.modelState == Ship.ModelState.Battle && candidate.generalCont != null && candidate.hull != null
        && source.IsEnemyFor(candidate);

    private static void SeedPrivateAim(AimMap map, PartData data, Ship anchor)
    {
        if (!map.ContainsKey(data)) map[data] = new Ship.Aim(data, anchor) { manual = true, progress = 1f };
        else { map[data].target = anchor; map[data].manual = true; }
    }

    private static bool PointEligibilityPrefix(Ship __instance, Ship ship, Part gun, bool inWeaponsRange, ref bool __result)
    {
        Scope? scope = _scope;
        if (scope == null || scope.Disposed || __instance.Pointer != scope.Source.Pointer
            || ship == null || !scope.Anchors.ContainsKey(ship.Pointer) || gun?.data == null
            || !(scope.TorpedoAim ? gun.data.isTorpedo : gun.data.isGun)) return true;
        try
        {
            Interlocked.Increment(ref _pointEligibilityChecks);
            // Original IsEnemyFine requires IsVisibleFor even when its range flag is false.
            // Replace only this scoped eligibility read, retaining alive/enemy and actual point range.
            __result = !_faulted && UsableAnchor(scope.Source, ship) && gun.ship?.Pointer == scope.Source.Pointer;
            if (__result && inWeaponsRange)
            {
                Vector3 from = scope.Source.transform.position;
                float dx = scope.Point.x - from.x, dz = scope.Point.z - from.z;
                // Interop nullable value types are boxed objects. C# null is not an empty
                // Nullable<T>; the generated wrapper rejects it before native invocation.
                float range = gun.WeaponRange(new Il2CppSystem.Nullable<Ship.ShellType>());
                __result = Finite(from) && float.IsFinite(range) && range >= 0f && dx * dx + dz * dz <= range * range;
            }
            return false;
        }
        catch (Exception ex) { _faulted = true; Error("point_eligibility", ex.ToString()); __result = false; return false; }
    }

    private static bool PositionPrefix(Transform __instance, ref Vector3 __result)
    {
        Scope? scope = _scope;
        if (scope == null || scope.Disposed || _suspended != 0 || !scope.Transforms.Contains(__instance.Pointer)) return true;
        __result = scope.Point;
        Interlocked.Increment(ref _getterHits);
        return false;
    }

    private static bool ReloadGatePrefix(CountdownTimer __instance, ref bool __result)
    {
        if (_suspended == 0)
            for (CompletedGunGate? gate = _completedGunGate; gate != null; gate = gate.Previous)
                if (!gate.Disposed && gate.Timers.Contains(__instance.Pointer))
                {
                    __result = true;
                    Interlocked.Increment(ref _completedGunGateHits);
                    Interlocked.Increment(ref _reloadGateHits); // also satisfies an enclosing point hold-gate ABI observation
                    return false;
                }
        Scope? scope = _scope;
        if (scope == null || scope.Disposed || _suspended != 0) return true;
        bool otherWeapon = scope.OtherReloadTimers.Contains(__instance.Pointer);
        if (!otherWeapon && ((scope.AllowShot && !_faulted) || !scope.ReloadTimers.ContainsKey(__instance.Pointer))) return true;
        // AimAndShoot calls this exact scalar getter at 0x1F1CDE6, then skips a loading gun.
        // Only the scoped read is changed. The timer's duration/start/end are never touched.
        __result = true;
        Interlocked.Increment(ref _reloadGateHits);
        return false;
    }

    private static void TorpedoCreatePrefix(Part from, Vector3 torpedoStart, Vector3 direction,
        float duration, float speed, out TorpedoFlight? __state)
    {
        __state = null;
        Scope? scope = _scope;
        if (scope == null || scope.Disposed || !scope.TorpedoAim || from?.ship?.Pointer != scope.Source.Pointer
            || from.data?.isTorpedo != true) return;
        // Observation only: do not replace direction. Native tube fan, crew penalty, random inaccuracy,
        // direction magnitude, speed, randomized lifetime and physical collision/damage remain untouched.
        try
        {
            __state = new TorpedoFlight { Point = scope.Point, SourceId = scope.Source.id.ToString(), Direction = direction };
            Interlocked.Increment(ref _torpedoCreates);
            Emit("POINT_TORPEDO_CREATE", new { source = __state.SourceId, part = from.Pointer.ToInt64(),
                point = V(scope.Point), start = V(torpedoStart), direction = V(direction), duration, speed,
                stationaryPoint = scope.VelocitySwapped && scope.Private.Anchor.velocity.sqrMagnitude == 0f,
                nativeFanAndScatterPreserved = true, nativeCollisionDamagePreserved = true,
                boundary = "Launch observation only; no target callback or post-launch guidance." });
        }
        catch (Exception ex) { Interlocked.Increment(ref _torpedoReadErrors); Emit("POINT_TORPEDO_EVIDENCE_ERROR", new { phase = "create", error = ex.Message }); }
    }

    private static Exception? TorpedoCreateFinalizer(Torpedo __result, TorpedoFlight? __state, Exception? __exception)
    {
        if (__state == null) return __exception;
        try
        {
            if (__exception != null || __result == null)
            {
                Emit("POINT_TORPEDO_NATIVE_CREATE_FAILED", new { source = __state.SourceId, error = __exception?.Message,
                    boundary = "Native caller does not test Create return; original native accounting may still occur. No refund is applied." });
            }
            else
            {
                __state.Torpedo = __result;
                TorpedoFlights[__result.Pointer] = __state;
                ObserveTorpedo(__state, "native_create_return");
            }
        }
        catch (Exception ex) { Interlocked.Increment(ref _torpedoReadErrors); Emit("POINT_TORPEDO_EVIDENCE_ERROR", new { phase = "create_return", error = ex.Message }); }
        return __exception;
    }

    private static void ObserveTorpedoes()
    {
        if (TorpedoFlights.Count == 0) return;
        int suspended = _suspended; _suspended++;
        try
        {
            var registered = new HashSet<IntPtr>();
            if (Torpedo.AllTorpedo != null) foreach (Torpedo torpedo in Torpedo.AllTorpedo) if (torpedo != null) registered.Add(torpedo.Pointer);
            foreach (var pair in new List<KeyValuePair<IntPtr, TorpedoFlight>>(TorpedoFlights))
            {
                TorpedoFlight flight = pair.Value;
                try
                {
                    if (flight.Torpedo == null || !registered.Contains(pair.Key) || !flight.Torpedo || !flight.Torpedo.gameObject.activeInHierarchy)
                    { EndTorpedoEvidence(pair.Key, flight, "native object unregistered/inactive/unavailable"); continue; }
                    ObserveTorpedo(flight, "tick");
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref _torpedoReadErrors);
                    EndTorpedoEvidence(pair.Key, flight, "lifecycle read unavailable: " + ex.Message);
                }
            }
        }
        catch (Exception ex) { Interlocked.Increment(ref _torpedoReadErrors); Emit("POINT_TORPEDO_EVIDENCE_ERROR", new { phase = "tick", error = ex.Message }); }
        finally { _suspended = suspended; }
    }

    private static void EndTorpedoEvidence(IntPtr pointer, TorpedoFlight flight, string reason)
    {
        TorpedoFlights.Remove(pointer); Interlocked.Increment(ref _torpedoEnds);
        Emit("POINT_TORPEDO_END", new { torpedo = pointer.ToInt64(), source = flight.SourceId, point = V(flight.Point),
            lastPosition = V(flight.LastPosition), elapsed = flight.LastElapsed, duration = flight.LastDuration,
            samples = flight.Samples, impactObserved = false, reason,
            boundary = "Read-only lifetime evidence; disappearance does not identify collision or timeout." });
    }

    private static void ObserveTorpedo(TorpedoFlight flight, string stage)
    {
        try
        {
            Torpedo torpedo = flight.Torpedo;
            flight.LastPosition = torpedo.transform.position;
            if (torpedo.timer != null) { flight.LastElapsed = torpedo.timer.pastTime; flight.LastDuration = torpedo.timer.duration; }
            if (flight.Samples >= 8 || (flight.Samples > 0 && flight.LastElapsed < flight.Samples * 3f)) return;
            flight.Samples++; Interlocked.Increment(ref _torpedoSamples);
            Emit("POINT_TORPEDO_SAMPLE", new { torpedo = torpedo.Pointer.ToInt64(), source = flight.SourceId, stage,
                point = V(flight.Point), position = V(flight.LastPosition), forward = V(torpedo.transform.forward),
                inputDirection = V(flight.Direction), speed = torpedo.speed,
                elapsed = flight.LastElapsed, duration = flight.LastDuration, sample = flight.Samples, maxSamples = 8,
                postLaunchGuidance = false, impactObserved = false });
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _torpedoReadErrors);
            if (!flight.ErrorEmitted) { flight.ErrorEmitted = true; Emit("POINT_TORPEDO_EVIDENCE_ERROR", new { phase = "sample", error = ex.Message }); }
        }
    }

    private static bool InitPrefix(Shell __instance, ref bool __result, out int __state)
    {
        __state = _suspended;
        Scope? scope = _scope;
        if (scope == null || scope.Disposed) return true;
        bool matched = false;
        try
        {
            Part part = __instance._from_k__BackingField;
            if (part?.ship == null || part.ship.Pointer != scope.Source.Pointer || part.data?.isGun != true) return true;
            matched = true;
            Interlocked.Increment(ref _initAttempts);
            // Disable native target damage before doing any fallible delegate work.
            __instance.willHitTarget = false;
            if (_faulted || !scope.Verified || !scope.AllowShot || scope.TorpedoAim)
            { Reject(__instance, "point_aim_hold_fire"); __result = false; return false; }
            _suspended++;
            Ship anchor = __instance.target;
            if (anchor == null || !scope.Anchors.ContainsKey(anchor.Pointer)) anchor = First(scope.Anchors);
            if (anchor.state != Ship.State.Alive || anchor.generalCont == null)
                throw new InvalidOperationException("Point shot anchor is unavailable.");
            // Sample once using the same native pre-factory range origin as the preview.
            // Measured per-barrel physical launch spacing remains independent.
            if (!FleetGunDispersion.TrySample(part, FleetGunDispersion.RangeOrigin(part), scope.Point, out var dispersed, out object dispersionEvidence))
            {
                // A data/readiness failure belongs to this attempted shot, not the ABI bridge.
                // Previously this generic throw poisoned the shared gun/torpedo bridge for the
                // remainder of the process, even though native state had been restored.
                Emit("POINT_DISPERSION_UNAVAILABLE", new { source = scope.Source.id.ToString(), part = part.Pointer.ToInt64(),
                    evidence = dispersionEvidence, fatal = FleetGunDispersion.Faulted });
                if (FleetGunDispersion.Faulted) throw new InvalidOperationException("Native precision state is unsafe: " + FleetGunDispersion.FaultReason);
                Reject(__instance, "dispersion_profile_unavailable"); __result = false; return false;
            }
            Vector3 impactPoint = new(dispersed.ImpactPoint.X, dispersed.ImpactPoint.Y, dispersed.ImpactPoint.Z);
            var flight = new Flight { Pointer = __instance.Pointer, Shell = __instance, Source = scope.Source, Anchor = anchor,
                AimCenter = scope.Point, Point = impactPoint, DispersionEvidence = dispersionEvidence, FrozenDispersion = dispersed.Profile,
                TargetOwner = new Ship.__c__DisplayClass701_0 { shellTarget = anchor } };
            flight.CallbackOwner = new Ship.__c__DisplayClass701_1 {
                hitOffset = flight.Point - anchor.generalCont.transform.position,
                field_Public___c__DisplayClass701_0_0 = flight.TargetOwner };
            flight.Callback = Il2CppSystem.Delegate.CreateDelegate(
                Il2CppType.Of<Il2CppSystem.Func<Il2CppSystem.Nullable<Vector3>>>(),
                flight.CallbackOwner, "<GetCalcHitPoint>b__0", false, true)
                .Cast<Il2CppSystem.Func<Il2CppSystem.Nullable<Vector3>>>();
            var value = flight.Callback.Invoke(); // native invocation only; no managed callback trampoline
            if (!value.HasValue || !Near(value.Value, flight.Point))
                throw new InvalidOperationException("Native fixed-point callback verification failed.");
            _callbackAbi = "OBSERVED_NATIVE_RETURN";
            __instance.target = anchor;
            __instance.CalcHitPoint = flight.Callback;
            __instance.hitPrecalc = new Ship.HitPrecalc();
            var method = __instance.methodStore;
            method.WillHitEnemy = false; method.ShellTarget = anchor.id; method.ShootPoint = flight.Point;
            __instance.methodStore = method; // getter returns a boxed copy
            // Native factory gives every tube one Part-centred origin. Apply only verified per-barrel
            // transverse spacing once, before native Init rebuilds this shell's independent arc.
            int barrel = method._Barrel_k__BackingField;
            bool launchResolved = FleetBarrelLaunch.TryResolve(part, barrel, __instance.shellStart,
                out Vector3 launchStart, out object launchEvidence) && Finite(launchStart);
            if (launchResolved)
            {
                __instance.shellStart = launchStart;
                __instance.transform.position = launchStart;
                Interlocked.Increment(ref _barrelLaunchResolved);
            }
            else Interlocked.Increment(ref _barrelLaunchFallbacks);
            Emit("POINT_BARREL_LAUNCH", new { shell = __instance.Pointer.ToInt64(), source = scope.Source.id.ToString(),
                applied = launchResolved, evidence = launchEvidence });
            if (Flights.TryGetValue(__instance.Pointer, out Flight? previousFlight))
            {
                TryEndEvidence(previousFlight, "native_shell_reinitialized", true);
                FleetGunImpactBridge.Forget(previousFlight.Pointer, previousFlight.ImpactGeneration);
            }
            Flights[__instance.Pointer] = flight;
            Emit("POINT_SHELL_INIT", new { shell = __instance.Pointer.ToInt64(), source = scope.Source.id.ToString(),
                anchor = anchor.id.ToString(), part = part.Pointer.ToInt64(), barrel, aimCenter = V(flight.AimCenter), point = V(flight.Point),
                dispersion = flight.DispersionEvidence, willHitTarget = false });
            return true;
        }
        catch (Exception ex)
        {
            // If source inspection itself failed, the active synchronous point factory also fails closed.
            if (matched || scope != null)
            {
                _faulted = true; Error("init", ex.ToString()); Reject(__instance, "point_init_error");
                __result = false; return false;
            }
            return true;
        }
    }

    private static Exception? InitFinalizer(Shell __instance, bool __result, int __state, Exception? __exception)
    {
        _suspended = __state;
        if (Flights.ContainsKey(__instance.Pointer))
        {
            if (__exception != null || !__result) { Reject(__instance, "native_init_failed"); if (__exception != null) Error("native_init", __exception.ToString()); }
            else
            {
                Interlocked.Increment(ref _accepted);
                Flight flight = Flights[__instance.Pointer];
                TryObserve(flight, "native_init_return");
                int suspended = _suspended; _suspended++;
                try { flight.ImpactGeneration = FleetGunImpactBridge.Track(__instance, flight.Source, flight.Point); }
                finally { _suspended = suspended; }
                try { FleetSalvoOverlay.Started(flight.Pointer.ToInt64(), flight.ImpactGeneration,
                    flight.Source.Pointer.ToInt64(), __instance._from_k__BackingField.data.caliber, flight.FrozenDispersion, flight.Point); }
                catch (Exception ex) { Error("salvo_overlay_start", ex.Message); }
            }
        }
        return __exception;
    }

    private static bool UpdatePrefix(Shell __instance, out int __state)
    {
        __state = _suspended;
        if (!Flights.TryGetValue(__instance.Pointer, out Flight? flight)) return true;
        _suspended++;
        try
        {
            Refresh(flight);
            if (FleetGunImpactBridge.TryImpact(__instance, flight.ImpactGeneration, out FleetGunImpactBridge.Impact? impact))
            {
                flight.GeometryImpact = true; flight.NativeDamageReturned = impact?.NativeReturned == true;
                flight.ImpactVictim = impact?.Victim ?? "";
                ConsumeImpact(flight);
                return false;
            }
        }
        catch (Exception ex) { Error("shell_refresh", ex.ToString()); Reject(__instance, "point_anchor_unavailable"); return false; }
        return true;
    }

    private static Exception? UpdateFinalizer(Shell __instance, int __state, Exception? __exception)
    {
        try
        {
            if (Flights.TryGetValue(__instance.Pointer, out Flight? flight))
                TryObserve(flight, __exception == null ? "native_update_return" : "native_update_exception");
        }
        finally { _suspended = __state; }
        return __exception;
    }

    private static void SuspendPrefix(out int __state)
    {
        __state = _suspended;
        if (_scope == null) return;
        _suspended++;
        // Native AimAndShoot calls this on SOURCE: preserve normal firing self-revelation.
        // Suspension makes any geometry reads real; no hidden anchor discovery flag is written.
        Interlocked.Increment(ref _pointSpottingSuspends);
    }
    private static Exception? SuspendFinalizer(int __state, Exception? __exception)
    { _suspended = __state; return __exception; }

    public static void Tick()
    {
        RequireThread();
        ObserveTorpedoes();
        int suspended = _suspended; _suspended++;
        try
        {
            var registered = new HashSet<IntPtr>();
            if (Shell.AllShells != null) foreach (Shell shell in Shell.AllShells) if (shell != null) registered.Add(shell.Pointer);
            foreach (Flight flight in new List<Flight>(Flights.Values))
            {
                try
                {
                    if (flight.Shell == null || !registered.Contains(flight.Shell.Pointer) || !flight.Shell.gameObject.activeInHierarchy)
                    {
                        TryEndEvidence(flight, "native_object_unavailable_unregistered_or_inactive", false);
                        FleetGunImpactBridge.Forget(flight.Pointer, flight.ImpactGeneration);
                        Flights.Remove(flight.Pointer); Interlocked.Increment(ref _completed); continue;
                    }
                    Refresh(flight);
                }
                catch (Exception ex) { Error("tick", ex.ToString()); Reject(flight.Shell, "point_tick_failed"); }
            }
        }
        finally { _suspended = suspended; }
    }

    private static void Refresh(Flight flight)
    {
        flight.Shell.willHitTarget = false;
        if (flight.Anchor == null || flight.Anchor.state != Ship.State.Alive || flight.Anchor.generalCont == null)
            throw new InvalidOperationException("Point shot anchor despawned or sank; cancel without retargeting.");
        Vector3 offset = flight.Point - flight.Anchor.generalCont.transform.position;
        if (!Finite(offset)) throw new InvalidOperationException("Nonfinite point callback offset.");
        flight.CallbackOwner.hitOffset = offset;
        Interlocked.Increment(ref _refreshes);
    }

    public static void Reset()
    {
        RequireThread();
        while (_scope != null) _scope.Dispose();
        while (_completedGunGate != null) _completedGunGate.Dispose();
        foreach (Flight flight in new List<Flight>(Flights.Values)) Reject(flight.Shell, "point_bridge_reset");
        Flights.Clear(); PointSources.Clear(); FleetGunImpactBridge.Reset(); FleetSalvoOverlay.Reset(); _suspended = 0;
        // Torpedo tracking is evidence only. Reset never removes, redirects, or refunds a native torpedo.
        TorpedoFlights.Clear();
        // Faults are sticky for this process. A reset cannot silently rearm a failed ABI bridge.
    }

    public static object Snapshot() => new { installed = _installed, faulted = _faulted,
        getterAbi = _getterAbi, reloadGateAbi = _reloadGateAbi, nativeCallbackAbi = _callbackAbi, active = Flights.Count,
        enters = Interlocked.Read(ref _enters), getterHits = Interlocked.Read(ref _getterHits),
        reloadGateHits = Interlocked.Read(ref _reloadGateHits), completedGunGateHits = Interlocked.Read(ref _completedGunGateHits),
        barrelLaunchResolved = Interlocked.Read(ref _barrelLaunchResolved), barrelLaunchFallbacks = Interlocked.Read(ref _barrelLaunchFallbacks),
        initAttempts = Interlocked.Read(ref _initAttempts), accepted = Interlocked.Read(ref _accepted),
        blocked = Interlocked.Read(ref _blocked), errors = Interlocked.Read(ref _errors),
        completed = Interlocked.Read(ref _completed), refreshes = Interlocked.Read(ref _refreshes), lastError = _lastError,
        trajectorySamples = Interlocked.Read(ref _trajectorySamples), trajectoryEnds = Interlocked.Read(ref _trajectoryEnds),
        trajectoryReadErrors = Interlocked.Read(ref _trajectoryReadErrors),
        privatePointSources = PointSources.Count, privateAimRestores = Interlocked.Read(ref _privateAimRestores),
        pointEligibilityChecks = Interlocked.Read(ref _pointEligibilityChecks), pointSpottingSuspends = Interlocked.Read(ref _pointSpottingSuspends),
        internalAnchorSelections = Interlocked.Read(ref _internalAnchorSelections),
        torpedoEnters = Interlocked.Read(ref _torpedoEnters), torpedoVelocityOverrides = Interlocked.Read(ref _torpedoVelocityOverrides),
        torpedoVelocityRestores = Interlocked.Read(ref _torpedoVelocityRestores),
        torpedoCreates = Interlocked.Read(ref _torpedoCreates), torpedoActive = TorpedoFlights.Count,
        torpedoSamples = Interlocked.Read(ref _torpedoSamples), torpedoEnds = Interlocked.Read(ref _torpedoEnds),
        torpedoReadErrors = Interlocked.Read(ref _torpedoReadErrors),
        gunImpact = FleetGunImpactBridge.Snapshot(),
        runtimeAcceptance = "Experimental: install/getter/callback observations are not gameplay acceptance." };

    private static void ConsumeImpact(Flight flight)
    {
        Shell shell = flight.Shell;
        // Only the current Init generation is owned. An old cleanup cannot destroy a pooled new flight.
        if (!Flights.TryGetValue(flight.Pointer, out Flight? current) || !ReferenceEquals(current, flight)) return;
        TryObserve(flight, "geometry_native_impact_return");
        TryEndEvidence(flight, "geometry_confirmed_native_impact", false);
        FleetGunImpactBridge.Forget(flight.Pointer, flight.ImpactGeneration);
        Flights.Remove(flight.Pointer); Interlocked.Increment(ref _completed);
        void Try(string phase, Action action) { try { action(); } catch (Exception ex) { Error("impact_cleanup_" + phase, ex.ToString()); } }
        Try("damage_off", () => shell.willHitTarget = false);
        Try("disable", () => shell.enabled = false);
        Try("deactivate", () => shell.gameObject.SetActive(false));
        Try("unregister", () => Shell.AllShells?.Remove(shell));
        Try("destroy", () => UObject.Destroy(shell.gameObject));
    }

    private static void Reject(Shell? shell, string reason)
    {
        Interlocked.Increment(ref _blocked);
        if (shell == null) return;
        IntPtr pointer = shell.Pointer;
        if (Flights.TryGetValue(pointer, out Flight? flight))
        { FleetGunImpactBridge.Forget(pointer, flight.ImpactGeneration); TryEndEvidence(flight, reason, true); }
        Flights.Remove(pointer);
        // Shell.Create has already registered this exact shell before Init. Mirror Init's failure
        // cleanup explicitly; returning false alone leaks that registration. No refund or shot writes.
        void Try(string operation, Action action) { try { action(); } catch (Exception ex) { _faulted = true; Error("cleanup_" + operation, ex.ToString()); } }
        Try("damage_off", () => shell.willHitTarget = false);
        Try("disable", () => shell.enabled = false);
        Try("deactivate", () => shell.gameObject.SetActive(false));
        Try("unregister", () => Shell.AllShells?.Remove(shell));
        Try("destroy", () => UObject.Destroy(shell.gameObject));
        // The native bool gate normally avoids these factories entirely. Aggregate repeated
        // backup rejections in Snapshot instead of writing one log per barrel per frame.
        if (reason != "point_aim_hold_fire" || Interlocked.Read(ref _blocked) <= 3)
            Emit("POINT_SHELL_CANCELLED", new { shell = pointer.ToInt64(), reason, refunded = false });
    }

    /// <summary>Best-effort observation, isolated from the combat error/cancellation path.</summary>
    private static void TryObserve(Flight flight, string stage)
    {
        int suspended = _suspended; _suspended++;
        try
        {
            if (flight.Shell == null) return;
            Vector3 position = flight.Shell.transform.position;
            if (!Finite(position)) throw new InvalidOperationException("Nonfinite native shell position.");
            float dx = position.x - flight.Point.x, dz = position.z - flight.Point.z;
            float horizontal = MathF.Sqrt(dx * dx + dz * dz), seaVertical = MathF.Abs(position.y);
            float pointVertical = MathF.Abs(position.y - flight.Point.y);
            float pointDistance = MathF.Sqrt(horizontal * horizontal + pointVertical * pointVertical);
            if (!float.IsFinite(horizontal) || !float.IsFinite(pointDistance)) throw new InvalidOperationException("Nonfinite trajectory distance.");
            // Preserve the actual last position even if optional identity/anchor/timer reads fail at despawn.
            flight.LastPosition = position;
            flight.LastHorizontal = horizontal; flight.LastSeaVertical = seaVertical;
            flight.LastPointVertical = pointVertical; flight.LastPointDistance = pointDistance;
            flight.LastFrame = Time.frameCount; flight.LastObservedUtc = DateTime.UtcNow; flight.Observations++;
            flight.MinHorizontal = MathF.Min(flight.MinHorizontal, horizontal);
            flight.MinSeaVertical = MathF.Min(flight.MinSeaVertical, seaVertical);
            flight.MinPointDistance = MathF.Min(flight.MinPointDistance, pointDistance);
            if (seaVertical <= 2f) flight.MinHorizontalNearSea = MathF.Min(flight.MinHorizontalNearSea, horizontal);
            try
            {
                if (flight.SourceId.Length == 0) flight.SourceId = flight.Source.id.ToString();
                if (flight.AnchorId.Length == 0) flight.AnchorId = flight.Anchor.id.ToString();
            }
            catch (Exception ex) { EvidenceReadError(flight, "identity", ex); }
            flight.LastNativeTargetAvailable = false;
            try
            {
                // Native lastHitPoint is the callback target, not an impact report.
                Vector3 target = flight.Shell.lastHitPoint;
                if (!Finite(target)) throw new InvalidOperationException("Nonfinite native target point.");
                flight.LastNativeTarget = target; flight.LastNativeTargetAvailable = true;
            }
            catch (Exception ex) { EvidenceReadError(flight, "native_target", ex); }
            flight.LastAnchorAvailable = false;
            try
            {
                Vector3 anchorPosition = flight.Anchor.generalCont.transform.position;
                if (!Finite(anchorPosition)) throw new InvalidOperationException("Nonfinite anchor position.");
                flight.LastAnchorPosition = anchorPosition; flight.LastAnchorAvailable = true;
            }
            catch (Exception ex) { EvidenceReadError(flight, "anchor_position", ex); }
            flight.LastTimingAvailable = false;
            float elapsed = 0f, duration = 0f;
            try
            {
                CountdownTimer timer = flight.Shell.timer;
                if (timer != null)
                {
                    elapsed = timer.pastTime; duration = timer.duration;
                    if (!float.IsFinite(elapsed) || !float.IsFinite(duration)) throw new InvalidOperationException("Nonfinite native flight timing.");
                    flight.LastTimingAvailable = true;
                }
            }
            catch (Exception ex) { elapsed = duration = 0f; EvidenceReadError(flight, "flight_timing", ex); }
            float progress = duration > 0f ? Math.Clamp(elapsed / duration, 0f, 1f) : 0f;
            flight.LastElapsed = elapsed; flight.LastDuration = duration; flight.LastProgress = progress;
            if (flight.EndEmitted || flight.Samples >= SampleProgress.Length) return;
            bool due = flight.Samples == 0 || (duration > 0f ? progress >= SampleProgress[flight.Samples]
                : (DateTime.UtcNow - flight.EvidenceCreatedUtc).TotalSeconds >= flight.Samples * 2d);
            if (!due) return;
            flight.Samples++;
            Interlocked.Increment(ref _trajectorySamples);
            Emit("POINT_SHELL_SAMPLE", new { shell = flight.Pointer.ToInt64(), source = flight.SourceId, anchor = flight.AnchorId,
                sample = flight.Samples, maxSamples = SampleProgress.Length, stage, observation = Observation(flight),
                aimCenter = V(flight.AimCenter), point = V(flight.Point), seaPlaneY = 0f, nativeLastHitPointRole = "native callback target; not collision evidence" });
        }
        catch (Exception ex) { EvidenceReadError(flight, stage, ex); }
        finally { _suspended = suspended; }
    }

    private static object Observation(Flight flight) => new {
        observedUtc = flight.LastObservedUtc.ToString("O"), frame = flight.LastFrame,
        position = V(flight.LastPosition), y = flight.LastPosition.y,
        horizontalDistanceToPoint = flight.LastHorizontal, verticalDistanceToSeaPlane = flight.LastSeaVertical,
        verticalDistanceToPoint = flight.LastPointVertical, distanceToPoint = flight.LastPointDistance,
        elapsed = flight.LastTimingAvailable ? (float?)flight.LastElapsed : null,
        duration = flight.LastTimingAvailable ? (float?)flight.LastDuration : null,
        progress = flight.LastTimingAvailable ? (float?)flight.LastProgress : null,
        anchorPosition = flight.LastAnchorAvailable ? V(flight.LastAnchorPosition) : null,
        nativeLastHitPoint = flight.LastNativeTargetAvailable ? V(flight.LastNativeTarget) : null };

    private static void TryEndEvidence(Flight flight, string reason, bool bridgeCancelled)
    {
        FleetSalvoOverlay.Ended(flight.Pointer.ToInt64(), flight.ImpactGeneration);
        if (flight.EndEmitted) return;
        // Mark before a final read: it may refresh the last observation but cannot emit a ninth sample.
        flight.EndEmitted = true;
        try
        {
            TryObserve(flight, "lifecycle_end_last_read");
            Interlocked.Increment(ref _trajectoryEnds);
            Emit("POINT_SHELL_END", new { shell = flight.Pointer.ToInt64(), source = flight.SourceId, anchor = flight.AnchorId,
                aimCenter = V(flight.AimCenter), point = V(flight.Point), reason, bridgeCancelled, samples = flight.Samples, observations = flight.Observations,
                lastObservation = flight.Observations == 0 ? null : Observation(flight), seaPlaneY = 0f,
                minHorizontalDistanceToPoint = FiniteOrNull(flight.MinHorizontal),
                minDistanceToPoint = FiniteOrNull(flight.MinPointDistance),
                minVerticalDistanceToSeaPlane = FiniteOrNull(flight.MinSeaVertical),
                minHorizontalDistanceWhileWithin2mOfSeaPlane = FiniteOrNull(flight.MinHorizontalNearSea),
                impactObserved = flight.GeometryImpact, geometryIntersectionObserved = flight.GeometryImpact,
                nativeDamageReturned = flight.NativeDamageReturned, victim = flight.ImpactVictim,
                boundary = flight.GeometryImpact
                    ? "Native box or bounded authored-side envelope intersection dispatched to native damage; inspect geometry event for type. Armor may yield zero damage. No HP assignment."
                    : "Disappearance/inactivity and proximity samples do not establish water impact. Native lastHitPoint is a callback target." });
        }
        catch (Exception ex) { EvidenceReadError(flight, "end_event", ex); }
    }

    private static float? FiniteOrNull(float value) => float.IsFinite(value) ? value : null;
    private static void EvidenceReadError(Flight flight, string stage, Exception ex)
    {
        Interlocked.Increment(ref _trajectoryReadErrors);
        if (flight.ObservationErrorEmitted) return;
        flight.ObservationErrorEmitted = true;
        Emit("POINT_SHELL_SAMPLE_ERROR", new { shell = flight.Pointer.ToInt64(), stage, error = ex.Message,
            boundary = "Read-only evidence failed; native projectile behavior and bridge fault state were left unchanged." });
    }

    private static Ship First(Dictionary<IntPtr, Ship> anchors)
    { foreach (Ship ship in anchors.Values) return ship; throw new InvalidOperationException("No anchor."); }
    private static void RequireThread()
    { if (_mainThread == 0 || Environment.CurrentManagedThreadId != _mainThread) throw new InvalidOperationException("Point bridge requires its installing game thread."); }
    private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    private static bool Near(Vector3 a, Vector3 b) => Finite(a) && Finite(b) && (a - b).sqrMagnitude < .0025f;
    private static float[] V(Vector3 v) => new[] { v.x, v.y, v.z };
    private static void Error(string phase, string message)
    { Interlocked.Increment(ref _errors); _lastError = phase + ": " + message; Emit("POINT_BRIDGE_ERROR", new { phase, message }); }
    private static void Emit(string name, object data)
    { try { _emit?.Invoke(name, data); } catch { /* telemetry cannot decide whether a native projectile is safe */ } }
}
