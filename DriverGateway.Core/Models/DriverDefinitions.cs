using System.Collections.ObjectModel;

namespace DriverGateway.Core.Models;

public sealed record DriverDefinition(
    string Name,
    string DriverType,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<ChannelDefinition> Channels);

public sealed record ChannelDefinition(
    string Name,
    string Endpoint,
    IReadOnlyDictionary<string, string> Settings,
    RetryPolicyOptions RetryPolicy,
    IReadOnlyDictionary<string, int> ScanClasses,
    IReadOnlyList<DeviceDefinition> Devices)
{
    public int ResolveScanIntervalMs(string scanClass)
    {
        if (ScanClasses.TryGetValue(scanClass, out var configuredMs) && configuredMs > 0)
        {
            return configuredMs;
        }

        return 1_000;
    }
}

public sealed record DeviceDefinition(
    string Name,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<TagDefinition> Tags);

public sealed record TagDefinition(
    string DriverName,
    string DriverType,
    string ChannelName,
    string DeviceName,
    string TagName,
    string NodeIdentifier,
    string Address,
    TagDataType DataType,
    string ScanClass,
    WriteMode WriteMode,
    IReadOnlyDictionary<string, string> Settings)
{
    public static IReadOnlyDictionary<string, string> EmptySettings { get; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}
