using System.Text.Json;

namespace DriverGateway.LoadTests.ModbusTcp;

public sealed class ModbusLoadScenario
{
    public string Name { get; set; } = "modbus-mixed-load";
    public string Endpoint { get; set; } = "127.0.0.1:5020";
    public byte UnitId { get; set; } = 1;
    public bool UseLocalServer { get; set; }
    public int ConnectTimeoutMs { get; set; } = 2_000;
    public int RequestTimeoutMs { get; set; } = 2_500;
    public int ReconnectDelayMs { get; set; } = 250;
    public int AddressSpaceSize { get; set; } = 2_000;
    public int ReadQuantityMin { get; set; } = 4;
    public int ReadQuantityMax { get; set; } = 20;
    public int WriteMultipleQuantity { get; set; } = 4;
    public int JitterPercent { get; set; } = 20;
    public int RandomSeed { get; set; } = 42;
    public OperationMix Mix { get; set; } = new();
    public List<LoadStage> Stages { get; set; } =
    [
        new LoadStage { Name = "warmup", DurationSeconds = 30, TargetClients = 10, RequestIntervalMs = 300, LinearRamp = false }
    ];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("Scenario.name is required.");
        }

        if (string.IsNullOrWhiteSpace(Endpoint))
        {
            throw new InvalidOperationException("Scenario.endpoint is required.");
        }

        EndpointParser.Parse(Endpoint);

        if (UnitId is < 1 or > 247)
        {
            throw new InvalidOperationException("Scenario.unitId must be between 1 and 247.");
        }

        if (ConnectTimeoutMs <= 0 || RequestTimeoutMs <= 0 || ReconnectDelayMs < 0)
        {
            throw new InvalidOperationException("Timeout values must be positive (reconnectDelay can be 0).");
        }

        if (AddressSpaceSize <= 0 || AddressSpaceSize > ushort.MaxValue + 1)
        {
            throw new InvalidOperationException("Scenario.addressSpaceSize must be between 1 and 65536.");
        }

        if (ReadQuantityMin <= 0 || ReadQuantityMax <= 0 || ReadQuantityMin > ReadQuantityMax || ReadQuantityMax > 125)
        {
            throw new InvalidOperationException("Read quantity range must be 1..125 and min <= max.");
        }

        if (WriteMultipleQuantity <= 0 || WriteMultipleQuantity > 123)
        {
            throw new InvalidOperationException("writeMultipleQuantity must be between 1 and 123.");
        }

        if (JitterPercent is < 0 or > 80)
        {
            throw new InvalidOperationException("jitterPercent must be between 0 and 80.");
        }

        Mix.Validate();

        if (Stages.Count == 0)
        {
            throw new InvalidOperationException("At least one stage is required.");
        }

        foreach (var stage in Stages)
        {
            stage.Validate();
        }
    }

    public void ApplyDurationScale(double scale)
    {
        foreach (var stage in Stages)
        {
            var scaled = (int)Math.Round(stage.DurationSeconds * scale, MidpointRounding.AwayFromZero);
            stage.DurationSeconds = Math.Max(1, scaled);
        }
    }
}

public sealed class OperationMix
{
    public int ReadHoldingWeight { get; set; } = 70;
    public int ReadInputWeight { get; set; } = 20;
    public int WriteSingleWeight { get; set; } = 5;
    public int WriteMultipleWeight { get; set; } = 5;

    public void Validate()
    {
        if (ReadHoldingWeight < 0 || ReadInputWeight < 0 || WriteSingleWeight < 0 || WriteMultipleWeight < 0)
        {
            throw new InvalidOperationException("Operation weights cannot be negative.");
        }

        var total = ReadHoldingWeight + ReadInputWeight + WriteSingleWeight + WriteMultipleWeight;
        if (total <= 0)
        {
            throw new InvalidOperationException("Operation weights total must be greater than zero.");
        }
    }
}

public sealed class LoadStage
{
    public string Name { get; set; } = "stage";
    public int DurationSeconds { get; set; }
    public int TargetClients { get; set; }
    public int RequestIntervalMs { get; set; } = 200;
    public bool LinearRamp { get; set; } = true;
    public int ResizeTickMs { get; set; } = 1_000;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("Stage name is required.");
        }

        if (DurationSeconds <= 0)
        {
            throw new InvalidOperationException($"Stage '{Name}' durationSeconds must be greater than zero.");
        }

        if (TargetClients < 0)
        {
            throw new InvalidOperationException($"Stage '{Name}' targetClients cannot be negative.");
        }

        if (RequestIntervalMs < 0)
        {
            throw new InvalidOperationException($"Stage '{Name}' requestIntervalMs cannot be negative.");
        }

        if (ResizeTickMs <= 0)
        {
            throw new InvalidOperationException($"Stage '{Name}' resizeTickMs must be greater than zero.");
        }
    }
}

public static class ScenarioLoader
{
    public static ModbusLoadScenario Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Scenario file not found: {path}");
        }

        var json = File.ReadAllText(path);
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        };

        var scenario = JsonSerializer.Deserialize<ModbusLoadScenario>(json, options);
        if (scenario is null)
        {
            throw new InvalidOperationException("Scenario file could not be parsed.");
        }

        return scenario;
    }
}

public readonly record struct EndpointTarget(string Host, int Port);

public static class EndpointParser
{
    public static EndpointTarget Parse(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("Endpoint is empty.");
        }

        var normalized = endpoint.Trim();
        if (!normalized.StartsWith("modbus://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = $"modbus://{normalized}";
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("modbus", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            throw new InvalidOperationException($"Invalid endpoint '{endpoint}'. Expected host:port or modbus://host:port.");
        }

        if (uri.Port <= 0 || uri.Port > 65535)
        {
            throw new InvalidOperationException($"Invalid endpoint port in '{endpoint}'.");
        }

        return new EndpointTarget(uri.Host, uri.Port);
    }
}
