using System;
using System.Collections.Generic;
using System.Globalization;
using Il2Cpp;
using UnityEngine;
using NVector = System.Numerics.Vector3;
using MathG = FleetOps.Experimental.FleetFireSolutionGeometry;

namespace FleetOps.Experimental;

/// <summary>Advisory only. Caller supplies an explicitly native-selected enemy identity (or the last
/// such identity), never an arbitrary native weapon target. No fallback to Aim.target is performed.</summary>
internal static class FleetFireSolution
{
    internal sealed record Marker(string Kind, Vector3 Point, float Seconds, Color Color, string Label);
    internal sealed class Snapshot
    {
        internal Ship Source = null!, Target = null!;
        internal float CapturedRealtime;
        public IReadOnlyList<Marker> Markers { get; init; } = Array.Empty<Marker>();
        public string Summary { get; init; } = "";
        public object Evidence { get; init; } = new { available = false };
    }

    internal static Snapshot Build(Ship source, Ship selectedVisibleEnemy, string language)
    {
        bool zh = language?.StartsWith("zh",StringComparison.OrdinalIgnoreCase) == true;
        string phase = "source_validation";
        try
        {
            // Visibility precedes position, velocity, bounds, name and weapon computation.
            if (!ValidateSource(source,out string sourceReason,out string? sourceError))
                return Empty(zh ? "己方选中舰不可用" : "Selected source unavailable",sourceReason,phase,sourceError);
            phase = "enemy_visibility_validation";
            if (!ValidateEnemy(source,selectedVisibleEnemy,out string enemyReason,out string? enemyError))
                return Empty(zh ? "请明确选择可见敌舰" : "Explicitly select a visible enemy",enemyReason,phase,enemyError);
            phase = "visible_native_motion";
            Vector3 own = source.transform.position, ownVelocity = source.velocity;
            Vector3 target = selectedVisibleEnemy.transform.position, velocity = selectedVisibleEnemy.velocity;
            if (!MathG.Finite(N(own)) || !MathG.Finite(N(ownVelocity)) || !MathG.Finite(N(target)) || !MathG.Finite(N(velocity)))
                return Empty(zh ? "原生运动数据不可用" : "Native motion unavailable","NONFINITE_MOTION",phase);
            var markers = new List<Marker>(); var details = new List<object>();
            string vessel = VesselLabel(source);
            Part? gun = null, torpedo = null;
            phase = "weapon_scan";
            if (source.parts != null) foreach (Part part in source.parts)
            {
                try
                {
                    if (part?.data?.isGun == true && gun == null && source.IsMainCal(part)) gun = part;
                    if (part?.data?.isTorpedo == true && torpedo == null) torpedo = part;
                }
                catch (Exception ex) { details.Add(new { kind = "WEAPON_SCAN", available = false, reason = "NATIVE_READ_EXCEPTION", phase, exceptionType = ex.GetType().FullName, error = ex.ToString() }); }
                if (gun != null && torpedo != null) break;
            }
            if (gun != null)
            {
                // Each weapon category owns its exception boundary: a torpedo read must
                // never erase a gun marker already computed successfully.
                try
                {
                    if (TryGun(source,gun,target,velocity,out Vector3 aim,out float seconds,out string reason,out object diagnostic))
                    {
                        bool? arc = FleetTorpedoOverlay.ReadArc(gun,aim-gun.transform.position);
                        markers.Add(new Marker("GUN",aim,seconds,new Color(.25f,.65f,1,1),
                            vessel+" "+CaliberLabel(gun,source)+" "+(zh ? "火炮 " : "Gun ")+seconds.ToString("F1",CultureInfo.InvariantCulture)+" s"+ArcWarning(arc,zh)));
                        details.Add(new { kind = "GUN", available = true, part = gun.Pointer.ToInt64(), seconds,
                            point = V(aim), nativeArcShoot = arc, diagnostic,
                            method = "native Shell.CalcTrajectory timeToTarget fixed-point iteration; current ammo where available" });
                    }
                    else details.Add(new { kind = "GUN", available = false, reason, diagnostic });
                }
                catch (Exception ex) { details.Add(new { kind = "GUN", available = false, reason = "NATIVE_READ_EXCEPTION", phase = "gun_marker", exceptionType = ex.GetType().FullName, error = ex.ToString() }); }
            }
            if (torpedo != null)
            {
                string weaponPhase = "native_speed";
                try
                {
                    float speed = source.TorpedoSpeed(true);
                    weaponPhase = "weapon_range";
                    float range = torpedo.WeaponRange(new Il2CppSystem.Nullable<Ship.ShellType>());
                    weaponPhase = "native_origin";
                    Vector3 origin = FleetTorpedoOverlay.NativeOrigin(torpedo);
                    weaponPhase = "constant_velocity_intercept";
                    if (MathG.TryIntercept(N(origin),N(target),N(velocity),speed,range,out float seconds,out NVector aim))
                    {
                        Vector3 point = U(aim);
                        bool? arc = FleetTorpedoOverlay.ReadArc(torpedo,point-origin);
                        markers.Add(new Marker("TORPEDO",point,seconds,new Color(.8f,.4f,1,1),
                            vessel+" "+CaliberLabel(torpedo,source)+" "+(zh ? "鱼雷 " : "Torp ")+seconds.ToString("F1",CultureInfo.InvariantCulture)+" s"+ArcWarning(arc,zh)));
                        details.Add(new { kind = "TORPEDO", available = true, part = torpedo.Pointer.ToInt64(), seconds,
                            point = V(point), speed, nativeRange = range, nativeArcShoot = arc,
                            method = "constant-target-velocity XZ intercept, native TorpedoSpeed(true)/WeaponRange, no shooter-velocity inheritance" });
                    }
                    else details.Add(new { kind = "TORPEDO", available = false, reason = "NO_IN_RANGE_INTERCEPT", phase = weaponPhase,
                        speed = Number(speed), nativeRange = Number(range) });
                }
                catch (Exception ex) { details.Add(new { kind = "TORPEDO", available = false, reason = "NATIVE_READ_EXCEPTION", phase = weaponPhase, exceptionType = ex.GetType().FullName, error = ex.ToString() }); }
            }
            phase = "closing_speed";
            NVector relative = N(velocity)-N(ownVelocity), displacement = N(target)-N(own); displacement.Y = 0;
            if (!MathG.Finite(relative) || !MathG.Finite(displacement) || !float.IsFinite(displacement.LengthSquared())
                || !float.IsFinite(ownVelocity.magnitude)) return Empty(zh ? "运动数据超出显示范围" : "Motion exceeds display range","DERIVED_MOTION_OVERFLOW",phase);
            float closing = displacement.LengthSquared() > .000001f ? -NVector.Dot(relative,NVector.Normalize(displacement)) : 0;
            if (!float.IsFinite(closing)) return Empty(zh ? "接近速度不可用" : "Closing speed unavailable","NONFINITE_CLOSING_SPEED",phase);
            string summary = string.Format(CultureInfo.InvariantCulture,
                zh ? "{3} 建议{0} · 本舰{1:F1}/接近{2:F1} m/s · 首主炮/首鱼雷座；即时发射/匀速目标" : "{3} leads {0} · own {1:F1}/closing {2:F1} m/s · first main gun/torpedo mount; instant launch/constant target",
                markers.Count,ownVelocity.magnitude,closing,vessel);
            return new Snapshot { Source = source, Target = selectedVisibleEnemy, CapturedRealtime = Time.realtimeSinceStartup,
                Markers = markers.AsReadOnly(), Summary = summary,
                Evidence = new { available = markers.Count > 0, reason = markers.Count > 0 ? "AVAILABLE" : "NO_USABLE_WEAPON_SOLUTION", phase = "complete", source = source.Pointer.ToInt64(), target = selectedVisibleEnemy.Pointer.ToInt64(),
                    targetSelection = "Caller explicitly selected native enemy identity; may retain identity only across friendly selection.",
                    ownPosition = V(own), ownVelocity = V(ownVelocity), targetPosition = V(target), targetVelocity = V(velocity),
                    closingSpeed = closing, details, nativeSamplerCalls = 0,
                    boundary = "Advisory instantaneous launch only. No orders/fire/flight mutation, no shooter-velocity inheritance, no acceleration/turning/loading delay/random heading. Displayed source/target velocities are native simulation world units per second." } };
        }
        catch (Exception ex) { return Empty(zh ? "建议点原生读取失败" : "Fire-solution native read unavailable","NATIVE_READ_EXCEPTION",phase,ex.ToString(),ex.GetType().FullName); }
    }

    internal static void Draw(Snapshot? snapshot)
    {
        try
        {
            if (snapshot == null || !IsOwnedAliveSelected(snapshot.Source) || !VisibleEnemy(snapshot.Source,snapshot.Target)
                || Time.realtimeSinceStartup-snapshot.CapturedRealtime > .35f || Time.realtimeSinceStartup < snapshot.CapturedRealtime) return;
            foreach (Marker marker in snapshot.Markers) FleetDispersionOverlay.DrawWorldMarker(marker.Point,marker.Label,marker.Color,7);
        }
        catch { }
    }

    internal static bool IsOwnedAliveSelected(Ship? source) => ValidateSource(source,out _,out _);
    internal static bool ValidateSource(Ship? source, out string reason, out string? error)
    {
        string phase = "battle"; error = null;
        try
        {
            if (!GameManager.IsBattle) { reason = "NOT_IN_BATTLE"; return false; }
            phase = "source_native_object";
            if (source == null || !source) { reason = "SOURCE_UNAVAILABLE"; return false; }
            phase = "source_owner";
            if (source.player?.isMain != true) { reason = "SOURCE_NOT_OWNED"; return false; }
            phase = "source_state";
            if (source.state != Ship.State.Alive || source.modelState != Ship.ModelState.Battle) { reason = "SOURCE_NOT_LIVE_BATTLE_SHIP"; return false; }
            phase = "source_selection";
            if (G.ui?.selectedShips == null) { reason = "NATIVE_SELECTION_UNAVAILABLE"; return false; }
            foreach (Ship selected in G.ui.selectedShips) if (selected != null && selected && selected.Pointer == source.Pointer)
            { reason = "VALID_SOURCE"; return true; }
            reason = "SOURCE_NOT_SELECTED"; return false;
        }
        catch (Exception ex) { reason = "SOURCE_VALIDATION_EXCEPTION:"+phase; error = ex.ToString(); return false; }
    }
    internal static bool VisibleEnemy(Ship source, Ship? enemy) => ValidateEnemy(source,enemy,out _,out _);
    internal static bool ValidateEnemy(Ship source, Ship? enemy, out string reason, out string? error)
    {
        string phase = "enemy_native_object"; error = null;
        try
        {
            if (enemy == null || !enemy) { reason = "EXPLICIT_ENEMY_UNAVAILABLE"; return false; }
            phase = "enemy_state";
            if (enemy.state != Ship.State.Alive || enemy.modelState != Ship.ModelState.Battle) { reason = "ENEMY_NOT_LIVE_BATTLE_SHIP"; return false; }
            phase = "enemy_relationship";
            if (!source.IsEnemyFor(enemy)) { reason = "SELECTED_SHIP_NOT_ENEMY"; return false; }
            phase = "enemy_visibility";
            if (PlayerController.Instance == null) { reason = "PLAYER_CONTROLLER_UNAVAILABLE"; return false; }
            if (!enemy.IsVisibleFor(PlayerController.Instance)) { reason = "ENEMY_NOT_VISIBLE"; return false; }
            reason = "VALID_VISIBLE_ENEMY"; return true;
        }
        catch (Exception ex) { reason = "ENEMY_VALIDATION_EXCEPTION:"+phase; error = ex.ToString(); return false; }
    }
    private static bool TryGun(Ship source, Part gun, Vector3 target, Vector3 velocity,
        out Vector3 point, out float seconds, out string reason, out object diagnostic)
    {
        point = target; point.y = 0; seconds = 0; reason = "Native ballistic iteration unavailable.";
        var read = new Dictionary<string,object?> { ["phase"] = "native_origin" }; diagnostic = read;
        try
        {
            // Native Shell.Create turret origin follows GetCalcHitPoint's range origin
            // by this partVisualSize/right offset; barrel helper spacing is not guessed here.
            Vector3 origin = FleetGunDispersion.RangeOrigin(gun)+gun.transform.right*gun._partVisualSize_k__BackingField.size.x;
            read["phase"] = "current_ammo";
            // Native Nullable<T> is a boxed value wrapper. A C# null is not its
            // HasValue=false representation: the generated invoker unboxes it.
            var shellType = new Il2CppSystem.Nullable<Ship.ShellType>();
            if (source.ammo != null && source.ammo.ContainsKey(gun.data)) shellType = new Il2CppSystem.Nullable<Ship.ShellType>(source.ammo[gun.data].ShellType);
            var dragTune = new Il2CppSystem.Nullable<float>();
            read["nullableShellTypeNonNull"] = shellType != null;
            read["nullableShellTypeHasValue"] = shellType!.HasValue;
            read["nullableDragTuneNonNull"] = dragTune != null;
            read["nullableDragTuneHasValue"] = dragTune!.HasValue;
            read["phase"] = "weapon_range";
            float range = gun.WeaponRange(shellType);
            if (!MathG.Finite(N(origin)) || !float.IsFinite(range) || range <= 0) return false;
            read["nativeRange"] = range;
            for (int i = 0; i < 8; i++)
            {
                if ((point-origin).magnitude > range) { reason = "Predicted gun point exceeds native range."; return false; }
                read["phase"] = "native_ballistic_arc"; read["iteration"] = i;
                Shell.Arc arc = Shell.CalcTrajectory(gun.data,source,origin,point,dragTune,gun.data,shellType);
                if (arc == null || !arc.isPossible || !float.IsFinite(arc.timeToTarget) || arc.timeToTarget <= 0) return false;
                float t = arc.timeToTarget;
                if (!MathG.TryAdvance(N(target),N(velocity),t,out NVector next)) return false;
                point = U(next);
                if (i > 0 && Math.Abs(t-seconds) <= .02f)
                { seconds = t; reason = "CONVERGED"; read["phase"] = "complete"; return (point-origin).magnitude <= range; }
                seconds = t;
            }
            reason = "Native ballistic lead did not converge; no guessed marker."; return false;
        }
        catch (Exception ex) { reason = "NATIVE_READ_EXCEPTION"; read["exceptionType"] = ex.GetType().FullName; read["error"] = ex.ToString(); return false; }
    }
    private static Snapshot Empty(string summary, string reason, string phase, string? error = null, string? exceptionType = null)
        => new() { Summary = summary, Evidence = new { available = false, reason, phase, exceptionType, error } };
    private static float? Number(float x) => float.IsFinite(x) ? x : null;
    private static string ArcWarning(bool? arc, bool zh) => arc == true ? "" : arc == false
        ? (zh ? " (射界外)" : " (arc blocked)") : (zh ? " (射界未知)" : " (arc unknown)");
    private static string VesselLabel(Ship source)
    {
        try
        {
            string name = source.Name(false,false,false,false,true).Replace('\r',' ').Replace('\n',' ').Trim();
            if (name.Length > 20) name = name.Substring(0,20);
            return string.IsNullOrEmpty(name) ? "Ship#"+source.Pointer.ToInt64().ToString("X",CultureInfo.InvariantCulture) : name;
        }
        catch { return "Ship#"+source.Pointer.ToInt64().ToString("X",CultureInfo.InvariantCulture); }
    }
    private static string CaliberLabel(Part part, Ship source)
    {
        try
        {
            float mm;
            if (part.data.isTorpedo)
            {
                // Native helper returns installed diameter in inches (default string "15").
                string native = Part.GetTorpedoesDiameterNowInstall(source);
                if (!float.TryParse(native,NumberStyles.Float,CultureInfo.InvariantCulture,out float inch)) return "? mm";
                mm = inch*25.4f;
            }
            else mm = part.data.GetCaliber(source);
            return float.IsFinite(mm) && mm > 0 ? mm.ToString("F0",CultureInfo.InvariantCulture)+" mm" : "? mm";
        }
        catch { return "? mm"; }
    }
    private static NVector N(Vector3 p) => new(p.x,p.y,p.z);
    private static Vector3 U(NVector p) => new(p.X,p.Y,p.Z);
    private static float[] V(Vector3 p) => new[] { p.x,p.y,p.z };
}
