using System.Text.Json;

namespace DriverGateway.Host.OpcUa.Config;

internal sealed class GatewayConfigV2
{
    public List<DriverConfigV2> Drivers { get; init; } = [];
}

internal sealed class DriverConfigV2
{
    public string Name { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public Dictionary<string, JsonElement> Settings { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<ChannelConfigV2> Channels { get; init; } = [];
}

internal sealed class ChannelConfigV2
{
    public string Name { get; init; } = string.Empty;
    public string Endpoint { get; init; } = string.Empty;
    public RetryPolicyConfigV2 Retry { get; init; } = new();
    public Dictionary<string, int> ScanClasses { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonElement> Settings { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<DeviceConfigV2> Devices { get; init; } = [];
}

internal sealed class RetryPolicyConfigV2
{
    public int InitialDelayMs { get; init; } = 500;
    public int MaxDelayMs { get; init; } = 30_000;
    public double JitterFactor { get; init; } = 0.2d;
    public int HealthCheckIntervalMs { get; init; } = 5_000;
}

internal sealed class DeviceConfigV2
{
    public string Name { get; init; } = string.Empty;
    public Dictionary<string, JsonElement> Settings { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
    public List<TagConfigV2> Tags { get; init; } = [];
}

internal sealed class TagConfigV2
{
    public string Name { get; init; } = string.Empty;
    public string DataType { get; init; } = "Double";
    public string Address { get; init; } = string.Empty;
    public string ScanClass { get; init; } = "default";
    public WriteConfigV2 Write { get; init; } = new();
    public Dictionary<string, JsonElement> Settings { get; init; } =
        new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class WriteConfigV2
{
    public string Mode { get; init; } = "immediate";
}
