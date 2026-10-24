using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace FleetOps.Experimental;

/// <summary>Only accepted point-shot flights own marks. No timer guesses and no combat writes.</summary>
internal static class FleetSalvoOverlay
{
    private sealed record Shot(long Pointer, long Generation, long Source, long Volley, float Caliber,
        FleetGunDispersionGeometry.Profile Profile, Vector3 Impact);
    private static readonly Dictionary<long, long> Requests = new();
    private static readonly Dictionary<long, Shot> Flights = new();
    private static long sequence;

    internal static int ActiveShells => Flights.Count;
    internal static int ActiveVolleys => Flights.Values.Select(s => (s.Source, s.Volley)).Distinct().Count();

    internal static void BeginRequest(IEnumerable<long> sources)
    {
        long id = ++sequence;
        foreach (long source in sources) if (source != 0) Requests[source] = id;
    }

    internal static void Started(long pointer, long generation, long source, float caliber,
        FleetGunDispersionGeometry.Profile frozenProfile, Vector3 frozenImpact)
    {
        if (pointer == 0 || generation <= 0 || source == 0 || !Finite(frozenImpact) || !float.IsFinite(frozenProfile.Radius)) return;
        // Native pooling can reuse the address; replace only its current generation.
        if (!Requests.TryGetValue(source, out long volley)) Requests[source] = volley = ++sequence;
        Flights[pointer] = new(pointer, generation, source, volley, caliber, frozenProfile, frozenImpact);
    }

    internal static void Ended(long pointer, long generation)
    {
        if (Flights.TryGetValue(pointer, out Shot? shot) && shot.Generation == generation) Flights.Remove(pointer);
    }

    internal static void Reset() { Flights.Clear(); Requests.Clear(); }

    internal static void Draw(Func<string, string, string> text)
    {
        var contours = new List<FleetDispersionOverlay.Contour>();
        // Every group is built from frozen launch-time values; moving the cursor cannot change it.
        foreach (var group in Flights.Values.GroupBy(s => (s.Source, s.Volley, s.Caliber)).Take(64))
        {
            var distinct = group.GroupBy(s => (s.Profile.ImpactCenter, s.Profile.Radius)).ToArray();
            foreach (var disk in distinct)
            {
                Shot shot = disk.First();
                var center = shot.Profile.ImpactCenter;
                contours.Add(new() { WorldCenter = new(center.X, 0, center.Z), Along = Vector3.forward,
                    Across = Vector3.right, AlongRadius = shot.Profile.Radius, AcrossRadius = shot.Profile.Radius,
                    Color = new Color(1f, .48f, .12f, .9f),
                    Label = text($"已发 #{shot.Volley} {shot.Caliber:F0}mm · {group.Count()} 发在飞",
                        $"FIRED #{shot.Volley} {shot.Caliber:F0}mm · {group.Count()} airborne") });
                if (contours.Count >= 64) break;
            }
            if (contours.Count >= 64) break;
        }
        FleetDispersionOverlay.Draw(contours, 64);
        foreach (Shot shot in Flights.Values.Take(512))
            FleetDispersionOverlay.DrawWorldMarker(shot.Impact, "", new Color(1f, .62f, .15f, .85f), 4f);
    }

    internal static object Snapshot() => new { activeShells = ActiveShells, activeVolleys = ActiveVolleys,
        flights = Flights.Values.Select(s => new { shell = s.Pointer, generation = s.Generation, source = s.Source,
            volley = s.Volley, caliber = s.Caliber, radius = s.Profile.Radius,
            center = new[] { s.Profile.ImpactCenter.X, s.Profile.ImpactCenter.Y, s.Profile.ImpactCenter.Z },
            frozenImpact = new[] { s.Impact.x, s.Impact.y, s.Impact.z } }).ToArray() };

    private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
}
