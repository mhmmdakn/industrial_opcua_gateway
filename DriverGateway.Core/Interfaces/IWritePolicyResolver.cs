using DriverGateway.Core.Models;

namespace DriverGateway.Core.Interfaces;

public interface IWritePolicyResolver
{
    WriteMode Resolve(TagDefinition tag);
}
