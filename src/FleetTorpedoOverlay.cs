using System;
using System.Collections.Generic;
using System.Globalization;
using Il2Cpp;
using UnityEngine;
using NVector = System.Numerics.Vector3;
using MathG = FleetOps.Experimental.FleetFireSolutionGeometry;

namespace FleetOps.Experimental;

/// <summary>Current order preview only. Does not inspect launched Torpedo objects or sample RNG.</summary>
internal static class FleetTorpedoOverlay
{
    internal sealed record Line(Vector3 Start, Vector3 End, Color Color, float Thickness = 1.25f, bool SpreadOnly = false);
    internal sealed class Snapshot
    {
        internal Ship Source = null!;
        internal float CapturedRealtime;
        public long SourcePointer { get; init; }
        public Vector3 Point { get; init; }
        public IReadOnlyList<Line> Lines { get; init; } = Array.Empty<Line>();
        public IReadOnlyList<FleetDispersionOverlay.Contour> Contours { get; init; } = Array.Empty<FleetDispersionOverlay.Contour>();
        public string Summary { get; init; } = "";
        public object Evidence { get; init; } = new { available = false };
    }

    internal static Snapshot Build(Ship source, Vector3 currentPoint, string language)
    {
        bool zh = language?.StartsWith("zh",StringComparison.OrdinalIgnoreCase) == true;
        var lines = new List<Line>(); var parts = new List<object>(); var failures = new List<object>();
        string phase = "source_validation";
        try
        {
            if (!FleetFireSolution.ValidateSource(source,out string sourceReason,out string? sourceError))
                return Empty(zh ? "鱼雷预览不可用" : "Torpedo preview unavailable",sourceReason,phase,sourceError);
            phase = "point_validation";
            if (!MathG.Finite(N(currentPoint))) return Empty(zh ? "鱼雷点无效" : "Invalid torpedo point","NONFINITE_POINT",phase);
            phase = "native_speed";
            float speed = source.TorpedoSpeed(true);
            if (!float.IsFinite(speed) || speed <= 0) return Empty(zh ? "原生鱼雷速度不可用" : "Native torpedo speed unavailable","INVALID_NATIVE_SPEED",phase);
            phase = "native_spread_parameters";
            float inaccuracy = MonoBehaviourExt.Param("torpedo_inaccuracy",.01f);
            float techA = source.TechA("torpedo_accuracy");
            float crew = source.GetCrewStationPenalty(Ship.CrewStations.Torpedoes);
            float penalty = MonoBehaviourExt.Param("crew_torpedoes_penalty_accuracy",.25f);
            bool spread = MathG.TryRandomHalfAngle(inaccuracy,techA,crew,penalty,out float halfAngle,out float diskRadius);
            int count = 0, tubes = 0, failed = 0;
            phase = "source_parts";
            if (source.parts != null) foreach (Part part in source.parts)
            {
                if (part?.data?.isTorpedo != true) continue;
                if (count >= 8) break;
                string mountPhase = "weapon_range"; float? observedRange = null; int? observedTubes = null;
                try
                {
                    // Interop value-type wrappers must be non-null even for HasValue=false.
                    float range = part.WeaponRange(new Il2CppSystem.Nullable<Ship.ShellType>()); observedRange = Number(range);
                    mountPhase = "native_tube_count";
                    int n = part.data.barrels; observedTubes = n;
                    if (!float.IsFinite(range) || range <= 0 || n < 1 || n > 64)
                    {
                        failed++; failures.Add(new { part = part.Pointer.ToInt64(), phase = mountPhase,
                            reason = n < 1 || n > 64 ? "INVALID_NATIVE_TUBE_COUNT" : "INVALID_NATIVE_RANGE",
                            nativeRange = observedRange, nativeTubeCount = observedTubes }); continue;
                    }
                    mountPhase = "native_origin";
                    Vector3 origin = NativeOrigin(part), right = part.transform.right;
                    if (!MathG.Finite(N(origin)) || !MathG.Finite(N(right)))
                    { failed++; failures.Add(new { part = part.Pointer.ToInt64(), phase = mountPhase, reason = "NONFINITE_NATIVE_ORIGIN_OR_RIGHT" }); continue; }
                    mountPhase = "aim_direction";
                    Vector3 desired = currentPoint-origin; desired.y = 0;
                    if (desired.sqrMagnitude < 1)
                    { failed++; failures.Add(new { part = part.Pointer.ToInt64(), phase = mountPhase, reason = "POINT_WITHIN_ONE_METRE" }); continue; }
                    float distance = desired.magnitude;
                    if (!float.IsFinite(distance))
                    { failed++; failures.Add(new { part = part.Pointer.ToInt64(), phase = mountPhase, reason = "NONFINITE_DISTANCE" }); continue; }
                    float displayedDistance = Math.Min(distance,range);
                    bool? arc = ReadArc(part,desired);
                    bool within = distance <= range;
                    Color color = arc == true && within ? new Color(.15f,.95f,.85f,.8f) : new Color(1,.65f,.2f,.7f);
                    mountPhase = "tube_geometry";
                    for (int i = 0; i < n && lines.Count < 256; i++)
                    {
                        if (!MathG.TryTube(N(origin),N(currentPoint),N(right),n,i,out NVector launch,out NVector direction,out float c)) continue;
                        lines.Add(new Line(U(launch),U(launch+direction*displayedDistance),color));
                        if (spread && halfAngle > 0)
                        {
                            NVector left = MathG.RotateY(direction,-halfAngle), rightEdge = MathG.RotateY(direction,halfAngle);
                            Color faint = new(color.r,color.g,color.b,.25f);
                            if (lines.Count < 256) lines.Add(new Line(U(launch),U(launch+left*displayedDistance),faint,1,true));
                            if (lines.Count < 256) lines.Add(new Line(U(launch),U(launch+rightEdge*displayedDistance),faint,1,true));
                            NVector previous = launch+left*displayedDistance;
                            for (int segment = 1; segment <= 8 && lines.Count < 256; segment++)
                            {
                                NVector next = launch+MathG.RotateY(direction,-halfAngle+2*halfAngle*segment/8)*displayedDistance;
                                lines.Add(new Line(U(previous),U(next),faint,1,true)); previous = next;
                            }
                        }
                    }
                    count++; tubes += n;
                    parts.Add(new { part = part.Pointer.ToInt64(), tubes = n, origin = V(origin), nativeRange = range,
                        requestedDistance = distance, withinRange = within, nativeArcShoot = arc, displayedDistance });
                }
                catch (Exception ex)
                {
                    failed++; failures.Add(new { part = part.Pointer.ToInt64(), phase = mountPhase,
                        reason = "NATIVE_MOUNT_READ_EXCEPTION", nativeRange = observedRange, nativeTubeCount = observedTubes,
                        exceptionType = ex.GetType().FullName, error = ex.ToString() });
                }
            }
            string summary = string.Format(CultureInfo.InvariantCulture,
                zh ? "鱼雷 {0}座/{1}管 · {2:F1} m/s · {3}" : "Torpedoes {0} mounts/{1} tubes · {2:F1} m/s · {3}",
                count,tubes,speed,spread ? (zh ? "原生发射扇面/随机方向边界" : "Native launch fan/random heading bound")
                    : (zh ? "随机边界不可用（仅名义管线）" : "Random bound unavailable (nominal tubes only)"));
            return new Snapshot { Source = source, SourcePointer = source.Pointer.ToInt64(), CapturedRealtime = Time.realtimeSinceStartup,
                Point = currentPoint, Lines = lines.AsReadOnly(), Summary = summary,
                Evidence = new { available = count > 0, reason = count > 0 ? "AVAILABLE" : "NO_USABLE_MOUNTS", phase = "complete",
                    mounts = parts, failedMounts = failed, mountFailures = failures, speed,
                    randomBoundAvailable = spread, nativeInaccuracy = Number(inaccuracy), nativeTechA = Number(techA),
                    nativeCrewStationPenalty = Number(crew), crewAccuracyPenalty = Number(penalty),
                    nativeCrewScale = Number(crew < 1 ? 1-(1-crew)*penalty : 1),
                    crewOperator = "1 - (1 - crew) * penalty when crew < 1; otherwise 1. Native subss at 0x1F1F14B; intentionally not inverted by intuition.",
                    randomDiskRadius = Number(diskRadius), headingHalfAngleRadians = spread ? (float?)halfAngle : null,
                    nativeSamplerCalls = 0, history = false, seaPlaneY = 0,
                    boundary = "Instantaneous nominal native tube fan and initial random-heading support at min(point distance, native range); not drift, randomized lifetime, arming, reload or a hit guarantee.",
                    limits = new { mounts = 8, lines = 256 } } };
        }
        catch (Exception ex) { return Empty(zh ? "鱼雷预览读取失败" : "Torpedo preview read unavailable","NATIVE_READ_EXCEPTION",phase,ex.ToString(),ex.GetType().FullName); }
    }

    internal static void Draw(Snapshot? snapshot, bool showSpread = true)
    {
        try
        {
            if (snapshot == null || !FleetFireSolution.IsOwnedAliveSelected(snapshot.Source)
                || Time.realtimeSinceStartup-snapshot.CapturedRealtime > .35f || Time.realtimeSinceStartup < snapshot.CapturedRealtime) return;
            foreach (Line line in snapshot.Lines)
                if (showSpread || !line.SpreadOnly) FleetDispersionOverlay.DrawWorldLine(line.Start,line.End,line.Color,line.Thickness);
            FleetDispersionOverlay.DrawWorldMarker(snapshot.Point,snapshot.Summary,new Color(.15f,.95f,.85f,.9f));
        }
        catch { }
    }

    internal static Vector3 NativeOrigin(Part part)
    {
        Renderer end = part._gunsEnd_k__BackingField;
        Vector3 basis = end != null && end ? end.bounds.m_Center : part.transform.position;
        Vector3 origin = basis+part.transform.forward*15f;
        // Native torpedo base uses the source rigidbody's Y; the annotation is its XZ sea projection.
        origin.y = 0; return origin;
    }
    internal static bool? ReadArc(Part part, Vector3 direction)
    {
        try
        {
            if (part._fireSector_k__BackingField == null || direction.sqrMagnitude < .000001f) return null;
            Part.SectorStep step = part.GetSector(Quaternion.LookRotation(direction,Vector3.up));
            return step == null ? null : !step.isDiscarded && step.status == Part.SectorStep.Status.Shoot;
        }
        catch { return null; }
    }
    private static Snapshot Empty(string summary, string reason, string phase, string? error = null, string? exceptionType = null)
        => new() { Summary = summary, Evidence = new { available = false, reason, phase, exceptionType, error } };
    private static float? Number(float x) => float.IsFinite(x) ? x : null;
    private static NVector N(Vector3 p) => new(p.x,p.y,p.z);
    private static Vector3 U(NVector p) => new(p.X,p.Y,p.Z);
    private static float[] V(Vector3 p) => new[] { p.x,p.y,p.z };
}
