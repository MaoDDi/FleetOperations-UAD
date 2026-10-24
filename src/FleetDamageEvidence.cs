using System;
using System.Collections.Generic;
using System.Threading;
using HarmonyLib;
using Il2Cpp;
using UnityEngine;

namespace FleetOps.Experimental;

/// <summary>Read-only native hit telemetry. Never supplies damage or changes native arguments/state.</summary>
public static class FleetDamageEvidence
{
    public sealed record NativeDamageSnapshot(long? ShipPointer, string? ShipId, string? State,
        float[]? Position, float? HealthStatus, float? StructureDamageRatio, float? FloodingRatio,
        float? ModulesDamageRatio, long? ReceivedStatisticsPointer, int? ReceivedHits,
        float? ReceivedDamage, float? ReceivedGunDamage, float? ReceivedFireDamage,
        float? ReceivedFloodingDamage, float? ReceivedAmmoDetonationDamage, string[] ReadErrors);

    private sealed class HitState
    {
        internal NativeDamageSnapshot Before = null!;
        internal long? SourcePointer, PartPointer, ProjectilePointer, ProjectileFromPointer;
        internal bool? IsTorpedo, IsGun;
        internal float[]? SourcePosition, ProjectilePosition;
        internal string[] ReadErrors = Array.Empty<string>();
    }

    private static Action<string, object>? emit;
    private static bool installed;
    private static long results, readErrors;
    private static int errorReported;

    public static void Install(HarmonyLib.Harmony harmony, Action<string, object> emitEvent)
    {
        if (installed) return;
        emit = emitEvent;
        System.Reflection.MethodInfo? target = null, prefix = null, finalizer = null;
        try
        {
            target = AccessTools.Method(typeof(Ship), nameof(Ship.TakeHit), new[] { typeof(Part), typeof(Projectile) })
                ?? throw new MissingMethodException("Ship.TakeHit(Part, Projectile)");
            prefix = AccessTools.Method(typeof(FleetDamageEvidence), nameof(TakeHitPrefix));
            finalizer = AccessTools.Method(typeof(FleetDamageEvidence), nameof(TakeHitFinalizer));
            harmony.Patch(target, prefix: new HarmonyMethod(prefix), finalizer: new HarmonyMethod(finalizer));
            installed = true;
            SafeEmit("NATIVE_DAMAGE_EVIDENCE_INSTALLED", new { observationOnly = true,
                target = "Ship.TakeHit(Part, Projectile)", runtimeAcceptance = "NOT_RUN" });
        }
        catch (Exception ex)
        {
            // Even a partially installed observer must not make native combat depend on telemetry.
            if (target != null)
            {
                try { if (prefix != null) harmony.Unpatch(target, prefix); } catch { }
                try { if (finalizer != null) harmony.Unpatch(target, finalizer); } catch { }
            }
            ReadError("install", ex.Message);
        }
    }

    public static object Status() => new { installed, results = Interlocked.Read(ref results),
        readErrors = Interlocked.Read(ref readErrors), observationOnly = true };

    /// <summary>Unavailable/non-finite values are null; errors are isolated per field.</summary>
    public static NativeDamageSnapshot ReadShipDamage(Ship? ship)
    {
        var errors = new List<string>();
        if (ship is null)
        {
            AddError(errors, "ship", "null");
            return new(null, null, null, null, null, null, null, null,
                null, null, null, null, null, null, null, errors.ToArray());
        }
        long? pointer = Read(errors, "ship.pointer", () => ship.Pointer.ToInt64());
        string? id = ReadReference(errors, "ship.id", () => ship.id.ToString());
        string? state = ReadReference(errors, "ship.state", () => ship.state.ToString());
        float[]? position = ReadPosition(errors, "ship.position", () => ship.transform.position);
        float? health = ReadFloat(errors, "HealthStatus", ship.HealthStatus);
        float? structure = ReadFloat(errors, "StructureDamageRatio", ship.StructureDamageRatio);
        float? flooding = ReadFloat(errors, "FloodingRatio", ship.FloodingRatio);
        float? modules = ReadFloat(errors, "ModulesDamageRatio", ship.ModulesDamageRatio);
        var stats = ReadReference(errors, "receivedStatistics", () => ship._statisticsReceived_k__BackingField);
        if (stats is null)
            return new(pointer, id, state, position, health, structure, flooding, modules,
                null, null, null, null, null, null, null, errors.ToArray());
        return new(pointer, id, state, position, health, structure, flooding, modules,
            Read(errors, "received.pointer", () => stats.Pointer.ToInt64()),
            Read(errors, "received.hits", () => stats.hits),
            ReadFloat(errors, "received.damage", () => stats.damage),
            ReadFloat(errors, "received.gunDamage", () => stats.gunDamage),
            ReadFloat(errors, "received.fireDamage", () => stats.fireDamage),
            ReadFloat(errors, "received.floadingDamage", () => stats.floadingDamage),
            ReadFloat(errors, "received.ammoDetonationDamage", () => stats.ammoDetonationDamage), errors.ToArray());
    }

    private static void TakeHitPrefix(Ship __instance, Part from, Projectile projectile, out HitState? __state)
    {
        __state = null;
        try
        {
            var errors = new List<string>();
            var state = new HitState { Before = ReadShipDamage(__instance) };
            __state = state;
            state.PartPointer = Read(errors, "from.pointer", () => from.Pointer.ToInt64());
            state.SourcePointer = Read(errors, "from.ship.pointer", () => from.ship.Pointer.ToInt64());
            state.ProjectilePointer = Read(errors, "projectile.pointer", () => projectile.Pointer.ToInt64());
            state.ProjectileFromPointer = Read(errors, "projectile.from.pointer", () => projectile.from.Pointer.ToInt64());
            state.IsTorpedo = Read(errors, "from.data.isTorpedo", () => from.data.isTorpedo);
            state.IsGun = Read(errors, "from.data.isGun", () => from.data.isGun);
            state.SourcePosition = ReadPosition(errors, "source.position", () => from.ship.transform.position);
            state.ProjectilePosition = ReadPosition(errors, "projectile.position", () => projectile.transform.position);
            state.ReadErrors = errors.ToArray();
        }
        catch (Exception ex) { ReadError("prefix", ex.Message); }
    }

    private static Exception? TakeHitFinalizer(Ship __instance, HitState? __state, Exception? __exception)
    {
        try
        {
            var after = ReadShipDamage(__instance);
            var before = __state?.Before;
            Interlocked.Increment(ref results);
            SafeEmit("NATIVE_TAKE_HIT_RESULT", new {
                observationOnly = true, victimPointer = after.ShipPointer,
                sourcePointer = __state?.SourcePointer, partPointer = __state?.PartPointer,
                projectilePointer = __state?.ProjectilePointer, projectileFromPointer = __state?.ProjectileFromPointer,
                isTorpedo = __state?.IsTorpedo, isGun = __state?.IsGun,
                sourcePosition = __state?.SourcePosition, projectilePosition = __state?.ProjectilePosition,
                before, after, rawDelta = new {
                    healthStatus = Delta(before?.HealthStatus, after.HealthStatus),
                    structureDamageRatio = Delta(before?.StructureDamageRatio, after.StructureDamageRatio),
                    floodingRatio = Delta(before?.FloodingRatio, after.FloodingRatio),
                    modulesDamageRatio = Delta(before?.ModulesDamageRatio, after.ModulesDamageRatio),
                    receivedHits = before?.ReceivedHits is int hits && after.ReceivedHits is int afterHits
                        ? (long?)(afterHits - (long)hits) : null,
                    receivedDamage = Delta(before?.ReceivedDamage, after.ReceivedDamage),
                    receivedGunDamage = Delta(before?.ReceivedGunDamage, after.ReceivedGunDamage),
                    receivedFireDamage = Delta(before?.ReceivedFireDamage, after.ReceivedFireDamage),
                    receivedFloodingDamage = Delta(before?.ReceivedFloodingDamage, after.ReceivedFloodingDamage),
                    receivedAmmoDetonationDamage = Delta(before?.ReceivedAmmoDetonationDamage, after.ReceivedAmmoDetonationDamage)
                },
                statisticsReferenceUnchanged = before?.ReceivedStatisticsPointer is long stats && after.ReceivedStatisticsPointer is long afterStats
                    ? (bool?)(stats == afterStats) : null,
                nativeExceptionObserved = __exception != null,
                nativeExceptionType = __exception?.GetType().FullName, nativeException = __exception?.Message,
                contextReadErrors = __state?.ReadErrors, readErrorCount = Interlocked.Read(ref readErrors),
                boundary = "Raw native before/after evidence only. TakeHit entry is not proof of positive damage; delayed damage may occur later."
            });
        }
        catch (Exception ex) { ReadError("finalizer", ex.Message); }
        return __exception; // Preserve the exact native/original exception object, including null.
    }

    private static double? Delta(float? before, float? after) => before.HasValue && after.HasValue
        ? (double)after.Value - before.Value : null;

    private static T? Read<T>(List<string> errors, string field, Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception ex) { AddError(errors, field, ex.Message); return null; }
    }

    private static T? ReadReference<T>(List<string> errors, string field, Func<T?> read) where T : class
    {
        try { return read(); }
        catch (Exception ex) { AddError(errors, field, ex.Message); return null; }
    }

    private static float? ReadFloat(List<string> errors, string field, Func<float> read)
    {
        float? value = Read(errors, field, read);
        if (value.HasValue && !float.IsFinite(value.Value))
        { AddError(errors, field, "non-finite"); return null; }
        return value;
    }

    private static float[]? ReadPosition(List<string> errors, string field, Func<Vector3> read)
    {
        Vector3? value = Read(errors, field, read);
        if (!value.HasValue) return null;
        Vector3 v = value.Value;
        if (!float.IsFinite(v.x) || !float.IsFinite(v.y) || !float.IsFinite(v.z))
        { AddError(errors, field, "non-finite"); return null; }
        return new[] { v.x, v.y, v.z };
    }

    private static void AddError(List<string> errors, string field, string message)
    { errors.Add(field + ": " + message); ReadError(field, message); }

    private static void ReadError(string phase, string message)
    {
        Interlocked.Increment(ref readErrors);
        if (Interlocked.Exchange(ref errorReported, 1) == 0)
            SafeEmit("NATIVE_DAMAGE_EVIDENCE_READ_ERROR", new { phase, error = message,
                boundary = "First telemetry error only; cumulative count remains available. Native state/call is untouched." });
    }

    private static void SafeEmit(string kind, object detail)
    {
        try { emit?.Invoke(kind, detail); }
        catch { Interlocked.Increment(ref readErrors); }
    }
}
