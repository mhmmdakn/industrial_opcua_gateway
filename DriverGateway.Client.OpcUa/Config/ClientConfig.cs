using System.Text.Json;

namespace DriverGateway.Client.OpcUa.Config;

internal sealed class ClientConfig
{
    public string Endpoint { get; init; } = "opc.tcp://localhost:4842/UA/DriverGateway";
    public SessionConfig Session { get; init; } = new();
    public List<string> ReadNodes { get; init; } = [];
    public List<string> SubscribeNodes { get; init; } = [];
    public List<WriteItemConfig> Writes { get; init; } = [];
}

internal sealed class SessionConfig
{
    public int SessionTimeoutMs { get; init; } = 60_000;
    public int ReconnectPeriodMs { get; init; } = 5_000;
    public int PublishingIntervalMs { get; init; } = 1_000;
    public int SamplingIntervalMs { get; init; } = 1_000;
}

internal sealed class WriteItemConfig
{
    public string NodeId { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public JsonElement Value { get; init; }
}
