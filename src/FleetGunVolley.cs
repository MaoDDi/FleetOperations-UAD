using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FleetOps.Experimental;

public sealed record GunPartShots(long Pointer, long Shots);
public sealed record GunVolleyPart(long Pointer, long BaselineShots, bool Completed);
public sealed record GunVolleyObservation(IReadOnlyCollection<long> NewlyCompletedIds, IReadOnlyCollection<long> CompletedIds, bool AllCompleted);

public sealed class GunVolleyBook
{
    private readonly Dictionary<long, Dictionary<long, GunVolleyPart>> entries = new();

    public IReadOnlyDictionary<long, IReadOnlyDictionary<long, GunVolleyPart>> Entries =>
        new ReadOnlyDictionary<long, IReadOnlyDictionary<long, GunVolleyPart>>(
            entries.ToDictionary(entry => entry.Key,
                entry => (IReadOnlyDictionary<long, GunVolleyPart>)new ReadOnlyDictionary<long, GunVolleyPart>(
                    new Dictionary<long, GunVolleyPart>(entry.Value))));

    // This captures one ship's weapon parts, never their point or expiry. The caller owns
    // native eligibility and clock policy. An empty replacement cancels a stale capture.
    public bool Request(long shipId, IEnumerable<GunPartShots> parts)
    {
        var captured = Materialize(parts);
        if (shipId == 0)
            return false;
        if (captured.Length == 0)
        {
            entries.Remove(shipId);
            return false;
        }
        entries[shipId] = captured.ToDictionary(part => part.Pointer,
            part => new GunVolleyPart(part.Pointer, part.Shots, false));
        return true;
    }

    public GunVolleyObservation Observe(long shipId, IEnumerable<GunPartShots> current)
    {
        // Fully read/validate before completing any part, even if an iterator fails midway.
        var readings = Materialize(current);
        var newlyCompleted = new List<long>();
        if (!entries.TryGetValue(shipId, out var parts))
            return new(Freeze(newlyCompleted), Freeze(Array.Empty<long>()), false);
        foreach (var reading in readings)
        {
            if (!parts.TryGetValue(reading.Pointer, out var part) || part.Completed || reading.Shots <= part.BaselineShots)
                continue;
            parts[reading.Pointer] = part with { Completed = true };
            newlyCompleted.Add(reading.Pointer);
        }
        return new(Freeze(newlyCompleted), CompletedPartIds(shipId), parts.Values.All(part => part.Completed));
    }

    // The returned gate mask is stable across later observations, requests and clears.
    public IReadOnlyCollection<long> CompletedPartIds(long shipId) =>
        Freeze(entries.TryGetValue(shipId, out var parts)
            ? parts.Values.Where(part => part.Completed).Select(part => part.Pointer)
            : Array.Empty<long>());

    public void Clear(IEnumerable<long> shipIds)
    {
        ArgumentNullException.ThrowIfNull(shipIds);
        var captured = shipIds.Where(id => id != 0).Distinct().ToArray();
        foreach (var id in captured)
            entries.Remove(id);
    }

    public void Clear() => entries.Clear();

    private static IReadOnlyCollection<long> Freeze(IEnumerable<long> ids) => Array.AsReadOnly(ids.ToArray());

    private static GunPartShots[] Materialize(IEnumerable<GunPartShots> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var captured = new Dictionary<long, GunPartShots>();
        foreach (var part in parts)
        {
            ArgumentNullException.ThrowIfNull(part);
            if (part.Pointer == 0)
                continue;
            if (part.Shots < 0)
                throw new ArgumentOutOfRangeException(nameof(parts), "Shot counters must be nonnegative.");
            captured.TryAdd(part.Pointer, part);
        }
        return captured.Values.ToArray();
    }
}
