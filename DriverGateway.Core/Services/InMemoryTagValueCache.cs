using System.Collections.Concurrent;
using DriverGateway.Core.Interfaces;
using DriverGateway.Core.Models;

namespace DriverGateway.Core.Services;

public sealed class InMemoryTagValueCache : ITagValueCache
{
    private readonly ConcurrentDictionary<string, TagValueSnapshot> _values =
        new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(TagValueSnapshot value)
    {
        _values[value.NodeIdentifier] = value;
    }

    public bool TryGet(string nodeIdentifier, out TagValueSnapshot value)
    {
        return _values.TryGetValue(nodeIdentifier, out value!);
    }
}
