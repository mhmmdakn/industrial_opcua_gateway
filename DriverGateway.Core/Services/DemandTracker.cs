using System.Collections.Concurrent;
using DriverGateway.Core.Interfaces;

namespace DriverGateway.Core.Services;

public sealed class DemandTracker : IDemandTracker
{
    private readonly ConcurrentDictionary<string, int> _subscriptionCounts =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTime> _oneShotExpirationsUtc =
        new(StringComparer.OrdinalIgnoreCase);

    public void AddSubscriptionDemand(string nodeIdentifier)
    {
        _subscriptionCounts.AddOrUpdate(nodeIdentifier, 1, (_, current) => current + 1);
    }

    public void RemoveSubscriptionDemand(string nodeIdentifier)
    {
        _subscriptionCounts.AddOrUpdate(
            nodeIdentifier,
            0,
            (_, current) =>
            {
                var next = current - 1;
                return next < 0 ? 0 : next;
            });

        if (_subscriptionCounts.TryGetValue(nodeIdentifier, out var value) && value <= 0)
        {
            _subscriptionCounts.TryRemove(nodeIdentifier, out _);
        }
    }

    public void RegisterOneShotDemand(string nodeIdentifier, TimeSpan ttl)
    {
        var effectiveTtl = ttl <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : ttl;
        var expiresAtUtc = DateTime.UtcNow.Add(effectiveTtl);
        _oneShotExpirationsUtc.AddOrUpdate(nodeIdentifier, expiresAtUtc, (_, previous) =>
            previous > expiresAtUtc ? previous : expiresAtUtc);
    }

    public IReadOnlySet<string> GetActiveDemand(DateTime utcNow)
    {
        var active = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (nodeIdentifier, count) in _subscriptionCounts)
        {
            if (count > 0)
            {
                active.Add(nodeIdentifier);
            }
        }

        foreach (var (nodeIdentifier, expiresAtUtc) in _oneShotExpirationsUtc)
        {
            if (expiresAtUtc > utcNow)
            {
                active.Add(nodeIdentifier);
                continue;
            }

            _oneShotExpirationsUtc.TryRemove(nodeIdentifier, out _);
        }

        return active;
    }
}
