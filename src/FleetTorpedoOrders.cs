using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FleetOps.Experimental;

public sealed record TorpedoPending(double ExpiresAt);

public sealed class TorpedoVolleyBook
{
    private readonly Dictionary<long, TorpedoPending> entries = new();

    // Stable snapshots allow a caller to consume/expire individual ships during enumeration.
    // Get/IsPending apply expiry using the caller's simulation clock; these getters have no clock.
    public IReadOnlyDictionary<long, TorpedoPending> Entries =>
        new ReadOnlyDictionary<long, TorpedoPending>(new Dictionary<long, TorpedoPending>(entries));
    public IReadOnlyCollection<long> PendingIds => Array.AsReadOnly(entries.Keys.ToArray());

    public void Request(IEnumerable<long> ids, double now)
    {
        ValidateTime(now);
        var selected = Selected(ids);
        foreach (var id in selected)
            entries[id] = new(now + 120);
    }

    public TorpedoPending? Get(long id, double now)
    {
        ValidateTime(now);
        if (!entries.TryGetValue(id, out var pending))
            return null;
        if (now < pending.ExpiresAt)
            return pending;
        entries.Remove(id);
        return null;
    }

    public bool IsPending(long id, double now) => Get(id, now) != null;
    public void WasFired(long id) => entries.Remove(id);

    public void Clear(IEnumerable<long> ids)
    {
        foreach (var id in Selected(ids))
            entries.Remove(id);
    }

    public void Clear() => entries.Clear();

    private static long[] Selected(IEnumerable<long> ids)
    {
        ArgumentNullException.ThrowIfNull(ids);
        // Capture fully before writing: interrupted native enumeration cannot issue a partial request.
        return ids.Where(id => id != 0).Distinct().ToArray();
    }

    private static void ValidateTime(double now)
    {
        if (!double.IsFinite(now))
            throw new ArgumentOutOfRangeException(nameof(now), "Timestamp must be finite.");
    }
}
