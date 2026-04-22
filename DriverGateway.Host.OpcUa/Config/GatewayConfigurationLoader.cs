using System.Text.Json;
using DriverGateway.Core.Models;

namespace DriverGateway.Host.OpcUa.Config;

internal static class GatewayConfigurationLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static bool TryLoad(
        string configPath,
        out IReadOnlyList<DriverDefinition> drivers,
        out IReadOnlyDictionary<string, TagDefinition> tagsByNodeId,
        out string error)
    {
        drivers = [];
        tagsByNodeId = new Dictionary<string, TagDefinition>(StringComparer.OrdinalIgnoreCase);
        error = string.Empty;

        try
        {
            if (!File.Exists(configPath))
            {
                error = $"Configuration file not found: {configPath}";
                return false;
            }

            var json = File.ReadAllText(configPath);
            var parsed = JsonSerializer.Deserialize<GatewayConfigV2>(json, JsonOptions) ?? new GatewayConfigV2();
            var compiledDrivers = Compile(parsed);
            var compiledTags = compiledDrivers
                .SelectMany(static driver => driver.Channels)
                .SelectMany(static channel => channel.Devices)
                .SelectMany(static device => device.Tags)
                .ToDictionary(static tag => tag.NodeIdentifier, StringComparer.OrdinalIgnoreCase);

            drivers = compiledDrivers;
            tagsByNodeId = compiledTags;
            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to load config '{configPath}': {ex.Message}";
            return false;
        }
    }

    private static IReadOnlyList<DriverDefinition> Compile(GatewayConfigV2 raw)
    {
        var output = new List<DriverDefinition>();

        foreach (var driver in raw.Drivers)
        {
            var driverName = driver.Name.Trim();
            var driverType = driver.Type.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(driverName) || string.IsNullOrWhiteSpace(driverType))
            {
                continue;
            }

            var channels = new List<ChannelDefinition>();
            foreach (var channel in driver.Channels)
            {
                var channelName = channel.Name.Trim();
                if (string.IsNullOrWhiteSpace(channelName))
                {
                    continue;
                }

                var devices = new List<DeviceDefinition>();
                foreach (var device in channel.Devices)
                {
                    var deviceName = device.Name.Trim();
                    if (string.IsNullOrWhiteSpace(deviceName))
                    {
                        continue;
                    }

                    var tags = new List<TagDefinition>();
                    foreach (var tag in device.Tags)
                    {
                        var tagName = tag.Name.Trim();
                        if (string.IsNullOrWhiteSpace(tagName))
                        {
                            continue;
                        }

                        var nodeIdentifier = $"{channelName}.{deviceName}.{tagName}";
                        tags.Add(new TagDefinition(
                            DriverName: driverName,
                            DriverType: driverType,
                            ChannelName: channelName,
                            DeviceName: deviceName,
                            TagName: tagName,
                            NodeIdentifier: nodeIdentifier,
                            Address: tag.Address.Trim(),
                            DataType: ParseDataType(tag.DataType),
                            ScanClass: string.IsNullOrWhiteSpace(tag.ScanClass) ? "default" : tag.ScanClass.Trim(),
                            WriteMode: ParseWriteMode(tag.Write.Mode),
                            Settings: FlattenSettings(tag.Settings)));
                    }

                    devices.Add(new DeviceDefinition(
                        Name: deviceName,
                        Settings: FlattenSettings(device.Settings),
                        Tags: tags));
                }

                var scanClasses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
                {
                    ["default"] = 1000
                };
                foreach (var (scanClassName, intervalMs) in channel.ScanClasses)
                {
                    if (string.IsNullOrWhiteSpace(scanClassName))
                    {
                        continue;
                    }

                    scanClasses[scanClassName.Trim()] = intervalMs <= 0 ? 1000 : intervalMs;
                }

                channels.Add(new ChannelDefinition(
                    Name: channelName,
                    Endpoint: channel.Endpoint.Trim(),
                    Settings: FlattenSettings(channel.Settings),
                    RetryPolicy: new RetryPolicyOptions(
                        channel.Retry.InitialDelayMs,
                        channel.Retry.MaxDelayMs,
                        channel.Retry.JitterFactor,
                        channel.Retry.HealthCheckIntervalMs),
                    ScanClasses: scanClasses,
                    Devices: devices));
            }

            output.Add(new DriverDefinition(
                Name: driverName,
                DriverType: driverType,
                Settings: FlattenSettings(driver.Settings),
                Channels: channels));
        }

        return output;
    }

    private static IReadOnlyDictionary<string, string> FlattenSettings(Dictionary<string, JsonElement> source)
    {
        var output = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in source)
        {
            output[key] = value.ValueKind switch
            {
                JsonValueKind.String => value.GetString() ?? string.Empty,
                JsonValueKind.Number => value.GetRawText(),
                JsonValueKind.True => bool.TrueString,
                JsonValueKind.False => bool.FalseString,
                JsonValueKind.Null => string.Empty,
                _ => value.GetRawText()
            };
        }

        return output;
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

    private static WriteMode ParseWriteMode(string rawWriteMode)
    {
        return rawWriteMode.Trim().ToLowerInvariant() switch
        {
            "queued" => WriteMode.Queued,
            _ => WriteMode.Immediate
        };
    }
}
