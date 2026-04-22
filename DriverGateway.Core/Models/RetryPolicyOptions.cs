namespace DriverGateway.Core.Models;

public sealed record RetryPolicyOptions(
    int InitialDelayMs = 500,
    int MaxDelayMs = 30_000,
    double JitterFactor = 0.2d,
    int HealthCheckIntervalMs = 5_000)
{
    public int SafeInitialDelayMs => InitialDelayMs <= 0 ? 500 : InitialDelayMs;
    public int SafeMaxDelayMs => MaxDelayMs <= 0 ? 30_000 : MaxDelayMs;
    public double SafeJitterFactor => JitterFactor < 0d ? 0d : JitterFactor;
    public int SafeHealthCheckIntervalMs => HealthCheckIntervalMs <= 0 ? 5_000 : HealthCheckIntervalMs;
}
