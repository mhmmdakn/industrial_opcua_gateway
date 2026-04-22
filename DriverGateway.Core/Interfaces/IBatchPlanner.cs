using DriverGateway.Core.Models;

namespace DriverGateway.Core.Interfaces;

public interface IBatchPlanner
{
    IReadOnlyList<ReadBatchPlan> BuildBatches(IReadOnlyCollection<TagDefinition> tags);
}
