namespace DriverGateway.Core.Models;

public sealed record TagValueSnapshot(
    string NodeIdentifier,
    object? Value,
    DateTime TimestampUtc,
    string Quality);

public sealed record TagReadResult(
    string NodeIdentifier,
    object? Value,
    DateTime TimestampUtc,
    string Quality = "Good");

public sealed record WriteResult(bool Success, string? Error = null)
{
    public static WriteResult Ok() => new(true);
    public static WriteResult Fail(string error) => new(false, error);
}

public sealed record ReadBatchPlan(string BatchKey, IReadOnlyList<TagDefinition> Tags);
