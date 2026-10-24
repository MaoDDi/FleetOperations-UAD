using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Numerics;

namespace FleetOps.Experimental;

public record FleetCandidate(long Pointer, string Id, bool Owned, bool Alive, bool InBattle);

public static class SelectionRules
{
    public static FleetCandidate[] Capture(IEnumerable<FleetCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var seen = new HashSet<long>();
        return candidates.Where(ship => ship.Pointer != 0 && !string.IsNullOrEmpty(ship.Id)
            && ship.Owned && ship.Alive && ship.InBattle && seen.Add(ship.Pointer)).ToArray();
    }
}

public static class SeaProjection
{
    public static bool TryProject(Vector3 origin, Vector3 direction, float maxDistance, out Vector3 point)
    {
        point = default;
        if (!Finite(origin) || !Finite(direction) || !float.IsFinite(maxDistance)
            || maxDistance <= 0 || direction.Y >= -1e-4f)
            return false;

        // Double intermediates avoid overflow for otherwise valid finite float rays.
        double t = -(double)origin.Y / direction.Y;
        if (!double.IsFinite(t) || t < 0)
            return false;
        var projected = new Vector3((float)(origin.X + t * direction.X), 0,
            (float)(origin.Z + t * direction.Z));
        if (!Finite(projected))
            return false;
        // Check the returned float point, including any final conversion rounding.
        double dx = (double)projected.X - origin.X;
        double dy = origin.Y;
        double dz = (double)projected.Z - origin.Z;
        if (dx * dx + dy * dy + dz * dz > (double)maxDistance * maxDistance)
            return false;
        point = projected;
        return true;
    }

    internal static bool Finite(Vector3 value) => float.IsFinite(value.X)
        && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

public enum FleetOrderMode { AimPoint, FireCurrent, FirePoint }
public record FleetOrder(FleetOrderMode Mode, Vector3? Point, double ExpiresAt);

public sealed class FleetOrderBook
{
    private readonly Dictionary<long, FleetOrder> entries = new();
    private readonly Dictionary<long, Vector3> followPoints = new();
    public IReadOnlyDictionary<long, FleetOrder> Entries { get; }

    public FleetOrderBook() => Entries = new ReadOnlyDictionary<long, FleetOrder>(entries);

    // Each caller gets a stable snapshot; a later selection cannot expand this capture.
    public IReadOnlyCollection<long> FollowingIds => Array.AsReadOnly(followPoints.Keys.ToArray());
    public bool IsFollowing(long id) => followPoints.ContainsKey(id);

    public bool ToggleFollow(IEnumerable<long> ids, Vector3 point, double now)
    {
        ValidateTime(now);
        ValidatePoint(point);
        var selected = Selected(ids);
        if (selected.Length == 0)
            return false;
        if (selected.All(followPoints.ContainsKey))
        {
            // Turning follow off does not revoke an already queued frozen volley.
            foreach (var id in selected)
                followPoints.Remove(id);
            return false;
        }
        foreach (var id in selected)
        {
            followPoints[id] = point;
            entries[id] = new(FleetOrderMode.AimPoint, point, double.PositiveInfinity);
        }
        return true;
    }

    public void UpdateFollow(Vector3 point, double now)
    {
        ValidateTime(now);
        ValidatePoint(point);
        foreach (var id in followPoints.Keys.ToArray())
        {
            followPoints[id] = point;
            if (entries.TryGetValue(id, out var order) && order.Mode != FleetOrderMode.AimPoint)
            {
                // Track until launch, without extending the original pending-fire deadline.
                // Previously captured records/flight vectors retain their own immutable point.
                if (order.Mode == FleetOrderMode.FirePoint)
                    entries[id] = order with { Point = point };
                continue;
            }
            entries[id] = new(FleetOrderMode.AimPoint, point, double.PositiveInfinity);
        }
    }

    public void Aim(IEnumerable<long> ids, Vector3 point, double now)
    {
        ValidateTime(now);
        ValidatePoint(point);
        var selected = Selected(ids);
        foreach (var id in selected)
        {
            followPoints.Remove(id);
            entries[id] = new(FleetOrderMode.AimPoint, point, double.PositiveInfinity);
        }
    }

    public void Fire(IEnumerable<long> ids, double now)
    {
        ValidateTime(now);
        var selected = Selected(ids);
        foreach (var id in selected)
        {
            var previous = Get(id, now);
            Vector3? point = followPoints.TryGetValue(id, out var following) ? following : previous?.Point;
            entries[id] = point.HasValue
                ? new(FleetOrderMode.FirePoint, point, now + 120)
                : new(FleetOrderMode.FireCurrent, null, now + 120);
        }
    }

    public void Restore(IEnumerable<long> ids)
    {
        foreach (var id in Selected(ids))
        {
            entries.Remove(id);
            followPoints.Remove(id);
        }
    }

    public FleetOrder? Get(long id, double now)
    {
        ValidateTime(now);
        if (!entries.TryGetValue(id, out var order))
            return null;
        if (order.Mode != FleetOrderMode.AimPoint && now >= order.ExpiresAt)
        {
            MarkFired(id);
            return entries.TryGetValue(id, out var restored) ? restored : null;
        }
        return order;
    }

    public void MarkFired(long id)
    {
        if (!entries.TryGetValue(id, out var order))
            return;
        if (order.Mode == FleetOrderMode.FirePoint)
            entries[id] = new(FleetOrderMode.AimPoint,
                followPoints.TryGetValue(id, out var following) ? following : order.Point,
                double.PositiveInfinity);
        else if (order.Mode == FleetOrderMode.FireCurrent)
            entries.Remove(id);
    }

    public void Clear()
    {
        entries.Clear();
        followPoints.Clear();
    }

    private static long[] Selected(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        // Materialize before mutating: an interrupted source cannot issue a partial command.
        return ids.Where(id => id != 0).Distinct().ToArray();
    }

    private static void ValidateTime(double now)
    {
        if (!double.IsFinite(now))
            throw new ArgumentOutOfRangeException(nameof(now), "Timestamp must be finite.");
    }

    private static void ValidatePoint(Vector3 point)
    {
        if (!SeaProjection.Finite(point))
            throw new ArgumentException("Aim point must have finite coordinates.", nameof(point));
    }
}
