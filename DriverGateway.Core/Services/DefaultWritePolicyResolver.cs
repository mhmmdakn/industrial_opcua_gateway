using DriverGateway.Core.Interfaces;
using DriverGateway.Core.Models;

namespace DriverGateway.Core.Services;

public sealed class DefaultWritePolicyResolver : IWritePolicyResolver
{
    public WriteMode Resolve(TagDefinition tag)
    {
        return tag.WriteMode;
    }
}
