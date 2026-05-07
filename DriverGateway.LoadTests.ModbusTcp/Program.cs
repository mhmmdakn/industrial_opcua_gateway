using DriverGateway.LoadTests.ModbusTcp;

var arguments = CommandLineArguments.Parse(args);
var scenarioPath = arguments.GetScenarioPath();
var scenario = ScenarioLoader.Load(scenarioPath);

if (arguments.EndpointOverride is not null)
{
    scenario.Endpoint = arguments.EndpointOverride;
}

if (arguments.UseLocalServerOverride.HasValue)
{
    scenario.UseLocalServer = arguments.UseLocalServerOverride.Value;
}

if (arguments.DurationScaleOverride.HasValue)
{
    scenario.ApplyDurationScale(arguments.DurationScaleOverride.Value);
}

scenario.Validate();

Console.WriteLine($"[loadtest] Scenario   : {scenario.Name}");
Console.WriteLine($"[loadtest] Endpoint   : {scenario.Endpoint}");
Console.WriteLine($"[loadtest] UnitId     : {scenario.UnitId}");
Console.WriteLine($"[loadtest] Stages     : {scenario.Stages.Count}");
Console.WriteLine($"[loadtest] LocalServer: {(scenario.UseLocalServer ? "ON" : "OFF")}");

using var rootCts = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    rootCts.Cancel();
};

await using var localServer = await LocalModbusServer.TryStartAsync(scenario, rootCts.Token).ConfigureAwait(false);

var runner = new LoadTestRunner(scenario);
var summary = await runner.RunAsync(rootCts.Token).ConfigureAwait(false);

Console.WriteLine();
Console.WriteLine("=== Load Test Summary ===");
Console.WriteLine($"Elapsed        : {summary.Elapsed}");
Console.WriteLine($"Requests total : {summary.TotalRequests}");
Console.WriteLine($"Success        : {summary.SuccessCount}");
Console.WriteLine($"Failures       : {summary.FailureCount}");
Console.WriteLine($"Timeouts       : {summary.TimeoutCount}");
Console.WriteLine($"Reconnects     : {summary.ReconnectCount}");
Console.WriteLine($"Throughput req/s: {summary.RequestsPerSecond:F2}");
Console.WriteLine($"Latency p50(ms): {summary.P50Ms:F2}");
Console.WriteLine($"Latency p95(ms): {summary.P95Ms:F2}");
Console.WriteLine($"Latency p99(ms): {summary.P99Ms:F2}");
Console.WriteLine();
Console.WriteLine("Per-operation:");
foreach (var operation in summary.PerOperation.OrderBy(static item => item.Key))
{
    Console.WriteLine(
        $"  {operation.Key,-14} total={operation.Value.Total,8} success={operation.Value.Success,8} fail={operation.Value.Failures,8}");
}
