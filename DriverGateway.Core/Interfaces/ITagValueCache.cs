using DriverGateway.Core.Models;

namespace DriverGateway.Core.Interfaces;

public interface ITagValueCache
{
    void Upsert(TagValueSnapshot value);
    bool TryGet(string nodeIdentifier, out TagValueSnapshot value);
}
