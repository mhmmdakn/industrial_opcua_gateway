using System.Text.Json;

namespace DriverGateway.Client.OpcUa.Config;

internal static class ClientConfigLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static bool TryLoad(string path, out ClientConfig config, out string error)
    {
        config = new ClientConfig();
        error = string.Empty;

        try
        {
            if (!File.Exists(path))
            {
                error = $"Config file not found: {path}";
                return false;
            }

            var json = File.ReadAllText(path);
            config = JsonSerializer.Deserialize<ClientConfig>(json, JsonOptions) ?? new ClientConfig();
        }
        catch (Exception ex)
        {
            error = $"Failed to parse config '{path}': {ex.Message}";
            return false;
        }

        var validationErrors = Validate(config);
        if (validationErrors.Count > 0)
        {
            error = string.Join(Environment.NewLine, validationErrors);
            return false;
        }

        return true;
    }

    private static List<string> Validate(ClientConfig config)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(config.Endpoint))
        {
            errors.Add("Config validation: endpoint is required.");
        }

        ValidateNodeList(config.ReadNodes, "readNodes", errors);
        ValidateNodeList(config.SubscribeNodes, "subscribeNodes", errors);

        for (var index = 0; index < config.Writes.Count; index++)
        {
            var write = config.Writes[index];
            var prefix = $"writes[{index}]";

            if (string.IsNullOrWhiteSpace(write.NodeId))
            {
                errors.Add($"Config validation: {prefix}.nodeId is required.");
            }
            else if (!NodeIdParser.TryParse(write.NodeId, out _))
            {
                errors.Add($"Config validation: {prefix}.nodeId is invalid: {write.NodeId}");
            }

            if (string.IsNullOrWhiteSpace(write.Type))
            {
                errors.Add($"Config validation: {prefix}.type is required.");
            }

            if (write.Value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                errors.Add($"Config validation: {prefix}.value is required.");
            }
        }

        return errors;
    }

    private static void ValidateNodeList(IReadOnlyList<string> nodes, string fieldName, ICollection<string> errors)
    {
        for (var index = 0; index < nodes.Count; index++)
        {
            var node = nodes[index];
            if (!NodeIdParser.TryParse(node, out _))
            {
                errors.Add($"Config validation: {fieldName}[{index}] is not a valid NodeId: {node}");
            }
        }
    }
}
