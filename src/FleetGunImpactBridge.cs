using System;
using System.Collections.Generic;
using System.Threading;
using Il2Cpp;
using UnityEngine;
using NVector = System.Numerics.Vector3;
using NMatrix = System.Numerics.Matrix4x4;
using G = FleetOps.Experimental.FleetGunImpactGeometry;
using H = FleetOps.Experimental.FleetNativeSideGeometry;

namespace FleetOps.Experimental;

/// <summary>A native box or bounded authored-side envelope gates one native armor/damage call.
/// Side envelopes are an approximation of native hit placement, not verified render-mesh collision. No HP assignment.</summary>
internal static class FleetGunImpactBridge
{
    private sealed class Trace
    {
        internal Shell Shell = null!;
        internal Ship Source = null!;
        internal Vector3 Point, LastPosition;
        internal float LastTime;
        internal IntPtr From, Timer, Arc, Trajectory;
        internal long Generation;
    }
    private sealed class Shape
    {
        internal G.Box Box;
        internal Part Part = null!;
        internal IntPtr Collider;
        internal string Role = "";
        internal H.Envelope? SideEnvelope;
        internal object? EnvelopeEvidence;
    }
    private sealed class Body
    {
        internal Ship Ship = null!;
        internal readonly List<Shape> Shapes = new();
        internal NVector Min = new(float.PositiveInfinity), Max = new(float.NegativeInfinity);
        internal int SideEnvelopes;
    }
    internal sealed class Impact
    {
        internal Vector3 Position;
        internal string Victim = "";
        internal bool NativeReturned;
    }
    private static readonly Dictionary<IntPtr, Trace> Traces = new();
    private static readonly List<Body> Bodies = new();
    private static readonly HashSet<IntPtr> ReportedReadErrors = new();
    private static readonly Dictionary<IntPtr, string> BodyEvidenceSignatures = new();
    private static readonly Dictionary<IntPtr, object> BodyCoverage = new();
    private static long _coverageEvents, _coverageReadErrors;
    private static Action<string, object>? _emit;
    private static int _geometryFrame = -1;
    private static long _generation, _tracked, _sweeps, _hits, _nativeCalls, _nativeReturns, _errors, _discontinuities, _boxes;
    private static long _sideEnvelopes, _sideEnvelopeHits, _sideEnvelopeRejected;

    internal static void Initialize(Action<string, object> emit) => _emit = emit;
    internal static long Track(Shell shell, Ship source, Vector3 point)
    {
        Forget(shell.Pointer); // a pooled native pointer never inherits a previous trace
        try
        {
            if (shell.arc?.trajectory == null || shell.timer == null || shell.from == null) return 0;
            Vector3 initial = At(shell, point, 0f);
            if (!Finite(initial) || !Near(initial, shell.transform.position))
            { Discontinuity(shell.Pointer, "init pose differs from native trajectory origin"); return 0; }
            Traces[shell.Pointer] = new Trace { Shell = shell, Source = source, Point = point,
                LastPosition = initial, LastTime = 0f, From = shell.from.Pointer, Timer = shell.timer.Pointer,
                Arc = shell.arc.Pointer, Trajectory = shell.arc.trajectory.Pointer, Generation = Interlocked.Increment(ref _generation) };
            Interlocked.Increment(ref _tracked);
            return Traces[shell.Pointer].Generation;
        }
        catch (Exception ex) { ReadError(shell.Pointer, "track", ex); return 0; }
    }
    private static void Forget(IntPtr shell) => Traces.Remove(shell);
    internal static void Forget(IntPtr shell, long generation)
    { if (Traces.TryGetValue(shell, out Trace? trace) && trace.Generation == generation) Traces.Remove(shell); }
    internal static void Reset() { Traces.Clear(); Bodies.Clear(); ReportedReadErrors.Clear(); BodyEvidenceSignatures.Clear(); BodyCoverage.Clear(); _geometryFrame = -1; }
    internal static object Snapshot() => new { tracked = _tracked, active = Traces.Count, sweeps = _sweeps,
        geometryHits = _hits, nativeDamageCalls = _nativeCalls, nativeDamageReturns = _nativeReturns,
        errors = _errors, discontinuities = _discontinuities, cachedBoxes = _boxes,
        cachedSideEnvelopes = _sideEnvelopes, sideEnvelopeHits = _sideEnvelopeHits, sideEnvelopeRejected = _sideEnvelopeRejected,
        bodyCoverage = new List<object>(BodyCoverage.Values), coverageEvents = _coverageEvents, coverageReadErrors = _coverageReadErrors,
        criterion = "native OBB or bounded native authored-side envelope intersection; no target-identity hit privilege; side envelope is not verified render-mesh collision", runtimeAcceptance = "NOT_RUN" };

    /// <summary>Visible native-box/side-envelope ray intersection for gun cursor input.
    /// Call outside a point AimAndShoot lease. Unspotted ships never disclose a cursor point or identity.</summary>
    internal static bool TryProjectCursorRay(Vector3 origin, Vector3 direction, float maxDistance,
        out Vector3 worldPoint, out string? targetIdentity)
    {
        worldPoint = default; targetIdentity = null;
        if (!Finite(origin) || !Finite(direction) || !float.IsFinite(maxDistance) || maxDistance <= 0f) return false;
        NVector rayDirection = N(direction);
        float length = rayDirection.Length();
        if (!float.IsFinite(length) || length < 1e-8f || PlayerController.Instance == null) return false;
        NVector a = N(origin), b = a + rayDirection * (maxDistance / length);
        if (!G.Finite(b)) return false;
        try
        {
            EnsureGeometry();
            bool found = false;
            G.Hit closest = default;
            Ship? victim = null;
            foreach (Body body in Bodies)
            {
                Ship ship = body.Ship;
                if (!ship || ship.state != Ship.State.Alive || ship.IsSurrendered || !ship.IsVisibleFor(PlayerController.Instance)) continue;
                foreach (Shape shape in body.Shapes)
                    if (Intersect(a, b, shape, out G.Hit hit) && hit.Fraction > 0f
                        && (!found || hit.Fraction < closest.Fraction))
                    { closest = hit; victim = ship; found = true; }
            }
            if (!found || victim == null) return false;
            worldPoint = U(closest.Point); targetIdentity = victim.id.ToString();
            return true;
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _coverageReadErrors);
            Emit("POINT_CURSOR_GEOMETRY_READ_ERROR", new { error = ex.Message,
                boundary = "Read-only hull cursor projection failed; caller retains sea-plane fallback." });
            return false;
        }
    }

    /// <summary>Called before native Shell.Update, using its exact clamped native trajectory time.
    /// Returns true only after consuming a native-box or bounded side-envelope hit; caller removes that exact own shell.</summary>
    internal static bool TryImpact(Shell shell, long generation, out Impact? impact)
    {
        impact = null;
        if (!Traces.TryGetValue(shell.Pointer, out Trace? trace) || trace.Generation != generation) return false;
        try
        {
            if (shell.from?.Pointer != trace.From || shell.timer?.Pointer != trace.Timer || shell.arc?.Pointer != trace.Arc
                || shell.arc?.trajectory?.Pointer != trace.Trajectory)
            { Discontinuity(shell.Pointer, "native source/timer/arc/trajectory identity changed"); return false; }
            float duration = shell.timer.duration, rawTime = shell.timer.pastTime;
            if (!float.IsFinite(duration) || duration < 0f || !float.IsFinite(rawTime))
            { Discontinuity(shell.Pointer, "nonfinite native time"); return false; }
            float time = Math.Clamp(rawTime, 0f, duration);
            var expected = new G.TraceStamp(trace.Generation, trace.From.ToInt64(), trace.Timer.ToInt64(), trace.Arc.ToInt64(), trace.Trajectory.ToInt64());
            var current = new G.TraceStamp(generation, shell.from.Pointer.ToInt64(), shell.timer.Pointer.ToInt64(), shell.arc.Pointer.ToInt64(), shell.arc.trajectory.Pointer.ToInt64());
            if (!G.CanContinueTrace(expected, current, trace.LastTime, time, duration, N(trace.LastPosition), N(shell.transform.position)))
            { Discontinuity(shell.Pointer, "time rewound or projectile pose reset/teleported"); return false; }
            if (time <= trace.LastTime) return false;
            EnsureGeometry();
            // Evaluate the unchanged native arc; short chords avoid tunneling at high game time scales.
            int count = Math.Clamp((int)MathF.Ceiling((time - trace.LastTime) / .05f), 1, 256);
            Vector3 a = trace.LastPosition;
            for (int i = 1; i <= count; i++)
            {
                float t = trace.LastTime + (time - trace.LastTime) * i / count;
                Vector3 b = At(shell, trace.Point, t);
                if (!Finite(a) || !Finite(b)) { Discontinuity(shell.Pointer, "nonfinite native trajectory position"); return false; }
                Interlocked.Increment(ref _sweeps);
                if (Closest(trace.Source, a, b, out Body? body, out Shape? shape, out G.Hit hit))
                {
                    // Remove before any fallible native damage call: no exception can cause a second dispatch.
                    Traces.Remove(shell.Pointer);
                    float previousT = trace.LastTime + (time - trace.LastTime) * (i - 1) / count;
                    float impactTime = previousT + (t - previousT) * hit.Fraction;
                    impact = Dispatch(trace, body!, shape!, hit, a, b, impactTime);
                    return true;
                }
                a = b;
            }
            trace.LastPosition = a; trace.LastTime = time;
        }
        catch (Exception ex)
        {
            Forget(shell.Pointer); ReadError(shell.Pointer, "sweep", ex);
            // Failure remains a native sea-point miss, never a hit on the internal calculation anchor.
        }
        return false;
    }

    private static Vector3 At(Shell shell, Vector3 point, float time) =>
        point - shell.originalTo + shell.arc.trajectory.PositionAtTime(time);

    private static bool Closest(Ship source, Vector3 a, Vector3 b, out Body? chosenBody, out Shape? chosenShape, out G.Hit chosenHit)
    {
        chosenBody = null; chosenShape = null; chosenHit = default;
        NVector na = N(a), nb = N(b);
        foreach (Body body in Bodies)
        {
            Ship ship = body.Ship;
            if (!ship || ship.Pointer == source.Pointer || ship.state != Ship.State.Alive || ship.IsSurrendered || !source.IsEnemyFor(ship)) continue;
            if (!G.TryIntersectSegment(na, nb, new G.Box(-1, NMatrix.Identity, (body.Min + body.Max) * .5f, (body.Max - body.Min) * .5f), out _)) continue;
            foreach (Shape shape in body.Shapes)
                if (Intersect(na, nb, shape, out G.Hit hit)
                    && (chosenBody == null || hit.Fraction < chosenHit.Fraction))
                { chosenBody = body; chosenShape = shape; chosenHit = hit; }
        }
        return chosenBody != null;
    }

    private static bool Intersect(NVector a, NVector b, Shape shape, out G.Hit hit) =>
        shape.SideEnvelope == null ? G.TryIntersectSegment(a, b, shape.Box, out hit)
            : H.TryIntersectSegment(a, b, shape.SideEnvelope, out hit);

    private static Impact Dispatch(Trace trace, Body body, Shape shape, G.Hit hit, Vector3 segmentStart, Vector3 segmentEnd, float time)
    {
        Shell shell = trace.Shell;
        Ship victim = body.Ship;
        Vector3 position = U(hit.Point), direction = shell.arc.trajectory.VelocityAtTime(time);
        var result = new Impact { Position = position, Victim = victim.id.ToString() };
        Interlocked.Increment(ref _hits);
        if (shape.SideEnvelope != null) Interlocked.Increment(ref _sideEnvelopeHits);
        try
        {
            if (!victim || victim.state != Ship.State.Alive || victim.IsSurrendered || !trace.Source.IsEnemyFor(victim)
                || shape.Part?.ship?.Pointer != victim.Pointer || !Finite(direction) || direction.sqrMagnitude < 1e-8f)
                throw new InvalidOperationException("Geometry victim/part/native impact direction no longer valid.");
            bool hull = shape.Part.data.isHull;
            Ship.Section section = hull ? victim.GetSectionFromPositions(victim.GetRelativePosition(victim.transform, position))
                : shape.Part.locatedInSection;
            if (section == null) section = victim.GetSectionFromPositions(victim.GetRelativePosition(victim.transform, position));
            if (section == null) throw new InvalidOperationException("No native section maps to the actual intersection.");
            bool side = shape.Role == "native_hull_side" || shape.SideEnvelope != null || (shape.Role != "native_hull_deck"
                && MathF.Abs(Vector3.Dot(U(hit.Normal), victim.generalCont.transform.up)) < .5f);
            // A native box or bounded authored-side intersection replaces only the preselected location. Native TakeHitRaw retains
            // penetration, actual armor, ricochet/overpenetration, AP/HE, damage/fire/flooding and statistics.
            var precalc = new Ship.HitPrecalc { isValid = true, sideHit = side,
                section = section, part = hull ? null! : shape.Part,
                willHitConningTower = false, willHitFireControl = false };
            shell.willHitTarget = false; // never enable delayed target/anchor damage
            shell.target = victim;
            shell.hitPrecalc = precalc;
            shell.transform.position = position;
            shell.transform.rotation = Quaternion.LookRotation(direction);
            float? beforeDamage = Damage(victim);
            int? beforeHits = Hits(victim);
            Emit("POINT_SHELL_GEOMETRY_HIT", new { shell = shell.Pointer.ToInt64(), generation = trace.Generation,
                source = trace.Source.id.ToString(), victim = result.Victim, part = shape.Part.Pointer.ToInt64(),
                collider = shape.Collider.ToInt64(), geometry = shape.Role, point = V(trace.Point), impact = V(position),
                normal = V(U(hit.Normal)), segmentStart = V(segmentStart), segmentEnd = V(segmentEnd), fraction = hit.Fraction,
                nativeSideEnvelope = shape.EnvelopeEvidence,
                sectionZ = section.z, sectionY = section.y, sideHit = side,
                nativeEntry = "Ship.TakeHit(Part, Projectile) -> Ship.TakeHitRaw", visibilityRequired = false });
            Interlocked.Increment(ref _nativeCalls);
            victim.TakeHit(shell.from, shell);
            Interlocked.Increment(ref _nativeReturns); result.NativeReturned = true;
            Emit("POINT_SHELL_NATIVE_DAMAGE_RETURN", new { shell = shell.Pointer.ToInt64(), victim = result.Victim,
                receivedDamageBefore = beforeDamage, receivedDamageAfter = Damage(victim), receivedHitsBefore = beforeHits, receivedHitsAfter = Hits(victim),
                boundary = "Native damage function returned; armor/ricochet may legitimately yield zero damage. No HP was assigned." });
        }
        catch (Exception ex) { ReadError(shell.Pointer, "native_impact", ex); }
        return result;
    }

    private static void EnsureGeometry()
    {
        int frame = Time.frameCount;
        if (_geometryFrame == frame) return;
        _geometryFrame = frame; Bodies.Clear(); _boxes = 0; _sideEnvelopes = 0;
        if (Ship.AllShips == null) return;
        foreach (Ship ship in Ship.AllShips)
        {
            if (ship == null || !ship || ship.state != Ship.State.Alive || ship.IsSurrendered || ship.hull == null) continue;
            var body = new Body { Ship = ship };
            var seen = new HashSet<IntPtr>();
            var envelopeSeen = new HashSet<IntPtr>();
            void Add(BoxCollider? collider, Part part, string role)
            {
                if (collider == null || !collider || !seen.Add(collider.Pointer)) return;
                try
                {
                    // These native armor/deck boxes may be disabled helper colliders. Read their actual
                    // center/size/transform, never enable them or use broad selection/camera capsules.
                    if (part.partSelect != null && collider.Pointer == part.partSelect.Pointer) return;
                    NMatrix matrix = Matrix(collider.transform.worldToLocalMatrix);
                    if (!NMatrix.Invert(matrix, out NMatrix localToWorld)) return;
                    NVector center = N(collider.center), half = N(collider.size) * .5f;
                    half = new(MathF.Abs(half.X), MathF.Abs(half.Y), MathF.Abs(half.Z));
                    if (!G.Finite(center) || !G.Finite(half)) return;
                    var shape = new Shape { Box = new G.Box(body.Shapes.Count, matrix, center, half),
                        Part = part, Collider = collider.Pointer, Role = role };
                    for (int x = -1; x <= 1; x += 2)
                        for (int y = -1; y <= 1; y += 2)
                            for (int z = -1; z <= 1; z += 2)
                            {
                                NVector corner = NVector.Transform(center + half * new NVector(x,y,z), localToWorld);
                                if (!G.Finite(corner)) return;
                                body.Min = NVector.Min(body.Min, corner); body.Max = NVector.Max(body.Max, corner);
                            }
                    body.Shapes.Add(shape); _boxes++;
                }
                catch (Exception ex) { ReadError(collider.Pointer, "geometry_box", ex); }
            }
            void AddSideEnvelope(BoxCollider? collider)
            {
                if (!VerifiedHullBorder(collider, ship.hull) || !envelopeSeen.Add(collider!.Pointer)) return;
                try
                {
                    // Same backing Bounds used by native side closure (+0x3B8). Native adds center.z
                    // to min.z/max.z, then multiplies the CLAMPED local Z by 1.07.
                    Bounds native = ship._hullSize_k__BackingField;
                    float lo = native.m_Center.z + (native.m_Center.z - native.m_Extents.z);
                    float hi = native.m_Center.z + (native.m_Center.z + native.m_Extents.z);
                    NMatrix boxToWorld = Matrix(collider!.transform.localToWorldMatrix);
                    NMatrix shipToLocal = Matrix(ship.transform.worldToLocalMatrix);
                    var spec = new H.Spec(body.Shapes.Count, N(collider.center), N(collider.size), boxToWorld, shipToLocal, lo, hi);
                    if (!H.TryBuild(spec, out H.Envelope? envelope, out H.Bounds bounds) || envelope == null)
                    { Interlocked.Increment(ref _sideEnvelopeRejected); return; }
                    var shape = new Shape { Part = ship.hull, Collider = collider.Pointer,
                        Role = "native_hull_side_envelope", SideEnvelope = envelope,
                        EnvelopeEvidence = new { collider = collider.Pointer.ToInt64(), center = V(collider.center), size = V(collider.size),
                            boxToWorld = M(boxToWorld), shipToLocal = M(shipToLocal), clampLo = lo, clampHi = hi,
                            hullBoundsCenter = V(native.m_Center), hullBoundsExtents = V(native.m_Extents),
                            alphaMax = envelope.AlphaMax, boundsMin = V(U(bounds.Min)), boundsMax = V(U(bounds.Max)),
                            nativeSubset = "u/v within actual half-size; world-Y interpolation; exact middle local-Z*1.07; clamped endcaps omitted",
                            physicalBoundary = "Authored native side envelope, not a verified external hull mesh." } };
                    body.Min = NVector.Min(body.Min, bounds.Min); body.Max = NVector.Max(body.Max, bounds.Max);
                    body.Shapes.Add(shape); body.SideEnvelopes++; _sideEnvelopes++;
                }
                catch (Exception ex) { ReadError(collider!.Pointer, "native_side_envelope", ex); }
            }
            try
            {
                if (ship.decks != null) foreach (var pair in ship.decks) Add(pair.Key, ship.hull, "native_hull_deck");
                if (ship.sidesLeft != null) foreach (var pair in ship.sidesLeft) Add(pair.Key, ship.hull, "native_hull_side");
                if (ship.sidesRight != null) foreach (var pair in ship.sidesRight) Add(pair.Key, ship.hull, "native_hull_side");
                if (ship.sidesLeft != null) foreach (var pair in ship.sidesLeft) AddSideEnvelope(pair.Key);
                if (ship.sidesRight != null) foreach (var pair in ship.sidesRight) AddSideEnvelope(pair.Key);
                // RefreshHull's side maps omit real bow/stern/edge borders. Keep the native maps'
                // classifications first, then admit only verified native armor-helper boxes.
                if (ship.hull.borders != null) foreach (Collider collider in ship.hull.borders)
                {
                    if (collider == null || seen.Contains(collider.Pointer)) continue;
                    BoxCollider? box = collider?.TryCast<BoxCollider>();
                    if (VerifiedHullBorder(box, ship.hull)) Add(box, ship.hull, "native_hull_border");
                }
                if (ship.parts != null) foreach (Part part in ship.parts)
                {
                    if (part?.data == null || part.data.isHull || part.borders == null) continue;
                    foreach (Collider collider in part.borders) Add(collider?.TryCast<BoxCollider>(), part, "native_part_border");
                }
                ObserveBodyCoverage(body);
                if (body.Shapes.Count > 0) Bodies.Add(body);
            }
            catch (Exception ex) { ReadError(ship.Pointer, "geometry_ship", ex); }
        }
    }

    private static bool VerifiedHullBorder(BoxCollider? box, Part hull)
    {
        if (box == null || !box || box.Pointer == hull.partSelect?.Pointer) return false;
        string layer = LayerMask.LayerToName(box.gameObject.layer);
        if (layer != "DeckBorder" && layer != "DeckWall") return false;
        string name = box.gameObject.name;
        if (!name.StartsWith(layer, StringComparison.Ordinal)) return false;
        // Do not promote preview/fake geometry or selection helpers to a combat hit shape.
        Transform? current = box.transform;
        for (int depth = 0; current != null && depth < 128; depth++, current = current.parent)
        {
            string ancestor = current.gameObject.name;
            if (ancestor.IndexOf("Fake", StringComparison.OrdinalIgnoreCase) >= 0
                || ancestor.IndexOf("PartSelect", StringComparison.OrdinalIgnoreCase) >= 0
                || LayerMask.LayerToName(current.gameObject.layer) == "TransparentFX") return false;
        }
        return current == null;
    }

    /// <summary>Read-only coverage audit; accepted borders were already added by EnsureGeometry.</summary>
    private static void ObserveBodyCoverage(Body body)
    {
        try
        {
            Ship ship = body.Ship;
            var roles = new Dictionary<string, int>();
            var included = new HashSet<IntPtr>();
            string signature = ship.id.ToString() + ":";
            foreach (Shape shape in body.Shapes)
            {
                roles[shape.Role] = roles.TryGetValue(shape.Role, out int count) ? count + 1 : 1;
                included.Add(shape.Collider); signature += shape.Role + ":" + shape.Collider.ToInt64() + ",";
            }
            int borders = ship.hull.borders?.Count ?? 0;
            signature += "borders=" + borders;
            BodyCoverage[ship.Pointer] = new { ship = ship.id.ToString(), pointer = ship.Pointer.ToInt64(),
                boxes = body.Shapes.Count - body.SideEnvelopes, shapes = body.Shapes.Count,
                sideEnvelopes = body.SideEnvelopes, roleCounts = roles, hullBorderCandidates = borders,
                nativeDeckCount = ship.decks?.Count ?? 0, nativeLeftSideCount = ship.sidesLeft?.Count ?? 0,
                nativeRightSideCount = ship.sidesRight?.Count ?? 0 };
            if (BodyEvidenceSignatures.TryGetValue(ship.Pointer, out string? old) && old == signature) return;
            BodyEvidenceSignatures[ship.Pointer] = signature;
            var candidates = new List<object>();
            var envelopeEvidence = new List<object>();
            foreach (Shape shape in body.Shapes) if (shape.EnvelopeEvidence != null) envelopeEvidence.Add(shape.EnvelopeEvidence);
            NVector candidateMin = new(float.PositiveInfinity), candidateMax = new(float.NegativeInfinity);
            int candidateBoxes = 0, includedCandidates = 0;
            if (ship.hull.borders != null) foreach (Collider collider in ship.hull.borders)
            {
                if (collider == null || !collider) continue;
                BoxCollider? box = collider.TryCast<BoxCollider>();
                bool alreadyIncluded = included.Contains(collider.Pointer);
                if (alreadyIncluded) includedCandidates++;
                object? bounds = null;
                if (box != null)
                {
                    NMatrix matrix = Matrix(box.transform.localToWorldMatrix);
                    NVector center = N(box.center), half = N(box.size) * .5f;
                    NVector min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
                    for (int x = -1; x <= 1; x += 2) for (int y = -1; y <= 1; y += 2) for (int z = -1; z <= 1; z += 2)
                    {
                        NVector corner = NVector.Transform(center + half * new NVector(x,y,z), matrix);
                        min = NVector.Min(min, corner); max = NVector.Max(max, corner);
                    }
                    if (G.Finite(min) && G.Finite(max))
                    {
                        candidateMin = NVector.Min(candidateMin, min); candidateMax = NVector.Max(candidateMax, max);
                        bounds = new { min = V(U(min)), max = V(U(max)) };
                    }
                    candidateBoxes++;
                }
                if (candidates.Count < 96) candidates.Add(new { collider = collider.Pointer.ToInt64(),
                    objectName = collider.gameObject.name, layer = collider.gameObject.layer,
                    layerName = LayerMask.LayerToName(collider.gameObject.layer), isBox = box != null,
                    collider.enabled, collider.isTrigger, active = collider.gameObject.activeInHierarchy,
                    alreadyIncluded, isPartSelect = collider.Pointer == ship.hull.partSelect?.Pointer,
                    eligibleHullBorder = VerifiedHullBorder(box, ship.hull),
                    center = box == null ? null : V(box.center), size = box == null ? null : V(box.size), worldBounds = bounds });
            }
            Interlocked.Increment(ref _coverageEvents);
            Emit("POINT_SHELL_GEOMETRY_BODY", new { frame = Time.frameCount,
                coverage = BodyCoverage[ship.Pointer], shownForOthers = ship._isShownForOthers_k__BackingField,
                shipPosition = V(ship.transform.position), hullActive = ship.hull.gameObject.activeInHierarchy,
                hullModelActive = ship.hull.model != null && ship.hull.model.gameObject.activeInHierarchy,
                includedWorldBounds = body.Shapes.Count == 0 ? null : new { min = V(U(body.Min)), max = V(U(body.Max)) },
                candidateBoxes, includedCandidates,
                hullBorderWorldBounds = candidateBoxes == 0 || !G.Finite(candidateMin) || !G.Finite(candidateMax)
                    ? null : new { min = V(U(candidateMin)), max = V(U(candidateMax)) },
                hullBorders = candidates, truncated = borders > candidates.Count,
                nativeSideEnvelopes = envelopeEvidence,
                boundary = "Native OBBs plus bounded authored-side envelopes. Envelopes follow native world-Y and middle local-Z mapping; complete rendered hull collision remains unverified." });
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _coverageReadErrors);
            Emit("POINT_SHELL_GEOMETRY_COVERAGE_ERROR", new { shipPointer = body.Ship.Pointer.ToInt64(), error = ex.Message,
                boundary = "Optional read-only audit failed; collision behavior unchanged." });
        }
    }

    private static void Discontinuity(IntPtr pointer, string reason)
    {
        Forget(pointer); Interlocked.Increment(ref _discontinuities);
        Emit("POINT_SHELL_SWEEP_DISCONTINUITY", new { shell = pointer.ToInt64(), reason,
            boundary = "Trace discarded; no cross-reset segment or native damage dispatched." });
    }
    private static float? Damage(Ship ship) { try { return ship.statisticsReceived?.damage; } catch { return null; } }
    private static int? Hits(Ship ship) { try { return ship.statisticsReceived?.hits; } catch { return null; } }
    private static void ReadError(IntPtr pointer, string phase, Exception ex)
    {
        Interlocked.Increment(ref _errors);
        if (ReportedReadErrors.Add(pointer)) Emit("POINT_SHELL_IMPACT_ERROR", new { pointer = pointer.ToInt64(), phase, error = ex.Message });
    }
    private static void Emit(string name, object data) { try { _emit?.Invoke(name, data); } catch { } }
    private static bool Near(Vector3 a, Vector3 b) => Finite(a) && Finite(b) && (a-b).sqrMagnitude < .25f;
    private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
    private static NVector N(Vector3 v) => new(v.x,v.y,v.z);
    private static Vector3 U(NVector v) => new(v.X,v.Y,v.Z);
    private static float[] V(Vector3 v) => new[] { v.x,v.y,v.z };
    private static float[] M(NMatrix m) => new[] { m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,
        m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44 };
    private static NMatrix Matrix(Matrix4x4 m) => new(m.m00,m.m10,m.m20,m.m30, m.m01,m.m11,m.m21,m.m31,
        m.m02,m.m12,m.m22,m.m32, m.m03,m.m13,m.m23,m.m33);
}

