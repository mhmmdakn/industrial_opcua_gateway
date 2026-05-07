namespace DriverGateway.LoadTests.ModbusTcp;

public sealed class CommandLineArguments
{
    private CommandLineArguments()
    {
    }

    public string? ScenarioPath { get; private init; }
    public string? EndpointOverride { get; private init; }
    public bool? UseLocalServerOverride { get; private init; }
    public double? DurationScaleOverride { get; private init; }

    public string GetScenarioPath()
    {
        if (!string.IsNullOrWhiteSpace(ScenarioPath))
        {
            return Path.GetFullPath(ScenarioPath);
        }

        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scenario.sample.json"));
    }

    public static CommandLineArguments Parse(string[] args)
    {
        string? scenario = null;
        string? endpoint = null;
        bool? localServer = null;
        double? durationScale = null;

        for (var index = 0; index < args.Length; index++)
        {
            var token = args[index];
            switch (token)
            {
                case "--scenario":
                {
                    scenario = RequireValue(args, ++index, token);
                    break;
                }
                case "--endpoint":
                {
                    endpoint = RequireValue(args, ++index, token);
                    break;
                }
                case "--local-server":
                {
                    localServer = true;
                    break;
                }
                case "--no-local-server":
                {
                    localServer = false;
                    break;
                }
                case "--duration-scale":
                {
                    var raw = RequireValue(args, ++index, token);
                    if (!double.TryParse(raw, out var parsed) || parsed <= 0)
                    {
                        throw new ArgumentException($"Invalid --duration-scale value '{raw}'. Use a positive decimal.");
                    }

                    durationScale = parsed;
                    break;
                }
                case "--help":
                case "-h":
                {
                    PrintHelpAndExit();
                    break;
                }
                default:
                {
                    throw new ArgumentException($"Unknown argument '{token}'. Use --help for available options.");
                }
            }
        }

        return new CommandLineArguments
        {
            ScenarioPath = scenario,
            EndpointOverride = endpoint,
            UseLocalServerOverride = localServer,
            DurationScaleOverride = durationScale
        };
    }

    private static string RequireValue(string[] args, int index, string option)
    {
        if (index >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        return args[index];
    }

    private static void PrintHelpAndExit()
    {
        Console.WriteLine("Modbus TCP Load Test");
        Console.WriteLine("Options:");
        Console.WriteLine("  --scenario <path>        Scenario JSON file path.");
        Console.WriteLine("  --endpoint <host:port>   Override endpoint from scenario.");
        Console.WriteLine("  --local-server           Force enable local in-process Modbus server.");
        Console.WriteLine("  --no-local-server        Force disable local in-process Modbus server.");
        Console.WriteLine("  --duration-scale <x>     Scale all stage durations (e.g. 0.1).");
        Console.WriteLine("  --help                   Show this help.");
        Environment.Exit(0);
    }
}
