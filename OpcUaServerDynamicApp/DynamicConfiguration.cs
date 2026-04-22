using System.Text.Json;

internal sealed class NodeConfiguration
{
    public List<ChannelConfig> Channels { get; init; } = [];
}

internal sealed class ChannelConfig
{
    public string Name { get; init; } = string.Empty;
    public List<DeviceConfig> Devices { get; init; } = [];
}

internal sealed class DeviceConfig
{
    public string Name { get; init; } = string.Empty;
    public List<TagConfig> Tags { get; init; } = [];
}

internal sealed class TagConfig
{
    public string Name { get; init; } = string.Empty;
    public string DataType { get; init; } = "Double";
    public string Access { get; init; } = "ReadWrite";
    public ProviderConfig Provider { get; init; } = new();
    public WritePolicyConfig WritePolicy { get; init; } = new();
}

internal sealed record ProviderConfig
{
    public string Type { get; init; } = "random";
    public int IntervalMs { get; init; } = 1000;
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public double? Start { get; init; }
    public bool? Wrap { get; init; }
    public double? Amplitude { get; init; }
    public double? Offset { get; init; }
    public double? PeriodSeconds { get; init; }
    public string? Value { get; init; }
}

internal sealed class WritePolicyConfig
{
    public string Mode { get; init; } = "ttl";
    public int TtlSeconds { get; init; } = 10;
}

internal enum TagDataType
{
    Double,
    Float,
    Boolean,
    Int16,
    UInt16,
    Word,
    Int32,
    UInt32,
    DWord,
    String
}

internal enum TagAccess
{
    ReadOnly,
    ReadWrite
}

internal sealed record TagDescriptor(
    string ChannelName,
    string DeviceName,
    string TagName,
    string NodeIdentifier,
    TagDataType DataType,
    TagAccess Access,
    ProviderConfig Provider,
    int WritePolicyTtlSeconds)
{
    public bool IsWritable => Access == TagAccess.ReadWrite;
}

internal static class NodeConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static bool TryLoad(string configPath, out NodeConfiguration configuration, out string error)
    {
        configuration = new NodeConfiguration();
        error = string.Empty;

        try
        {
            if (!File.Exists(configPath))
            {
                error = $"Configuration file not found: {configPath}";
                return false;
            }

            var json = File.ReadAllText(configPath);
            configuration = JsonSerializer.Deserialize<NodeConfiguration>(json, JsonOptions) ?? new NodeConfiguration();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to parse configuration: {ex.Message}";
            return false;
        }
    }
}

internal static class NodeConfigurationCompiler
{
    public static IReadOnlyDictionary<string, TagDescriptor> Compile(NodeConfiguration configuration)
    {
        var descriptors = new Dictionary<string, TagDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var channel in configuration.Channels)
        {
            var channelName = channel.Name.Trim();
            if (string.IsNullOrWhiteSpace(channelName))
            {
                continue;
            }

            foreach (var device in channel.Devices)
            {
                var deviceName = device.Name.Trim();
                if (string.IsNullOrWhiteSpace(deviceName))
                {
                    continue;
                }

                foreach (var tag in device.Tags)
                {
                    var tagName = tag.Name.Trim();
                    if (string.IsNullOrWhiteSpace(tagName))
                    {
                        continue;
                    }

                    var nodeIdentifier = $"{channelName}.{deviceName}.{tagName}";
                    var descriptor = new TagDescriptor(
                        channelName,
                        deviceName,
                        tagName,
                        nodeIdentifier,
                        ParseDataType(tag.DataType),
                        ParseAccess(tag.Access),
                        NormalizeProvider(tag.Provider),
                        NormalizeTtlSeconds(tag.WritePolicy));

                    descriptors[nodeIdentifier] = descriptor;
                }
            }
        }

        return descriptors;
    }

    private static ProviderConfig NormalizeProvider(ProviderConfig provider)
    {
        var interval = provider.IntervalMs <= 0 ? 1000 : provider.IntervalMs;
        return provider with { IntervalMs = interval };
    }

    private static int NormalizeTtlSeconds(WritePolicyConfig writePolicy)
    {
        if (!string.Equals(writePolicy.Mode, "ttl", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        return writePolicy.TtlSeconds <= 0 ? 10 : writePolicy.TtlSeconds;
    }

    private static TagDataType ParseDataType(string rawDataType)
    {
        return rawDataType.Trim().ToLowerInvariant() switch
        {
            "double" => TagDataType.Double,
            "float" or "single" or "real" => TagDataType.Float,
            "bool" or "boolean" => TagDataType.Boolean,
            "short" or "int16" => TagDataType.Int16,
            "ushort" or "uint16" => TagDataType.UInt16,
            "word" => TagDataType.Word,
            "int" or "int32" => TagDataType.Int32,
            "uint" or "uint32" => TagDataType.UInt32,
            "dword" => TagDataType.DWord,
            "string" => TagDataType.String,
            _ => TagDataType.Double
        };
    }

    private static TagAccess ParseAccess(string rawAccess)
    {
        return rawAccess.Trim().ToLowerInvariant() switch
        {
            "readonly" or "read" => TagAccess.ReadOnly,
            _ => TagAccess.ReadWrite
        };
    }
}
