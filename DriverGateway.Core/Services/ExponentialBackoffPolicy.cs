using DriverGateway.Core.Models;

namespace DriverGateway.Core.Services;

public sealed class ExponentialBackoffPolicy
{
    private readonly RetryPolicyOptions _options;
    private readonly Random _random = new();

    public ExponentialBackoffPolicy(RetryPolicyOptions options)
    {
        _options = options;
    }

    public TimeSpan NextDelay(int attempt)
    {
        var safeAttempt = Math.Max(0, attempt);
        var baseDelayMs = _options.SafeInitialDelayMs * Math.Pow(2d, safeAttempt);
        var boundedMs = Math.Min(baseDelayMs, _options.SafeMaxDelayMs);
        var jitterSpanMs = boundedMs * _options.SafeJitterFactor;

        if (jitterSpanMs <= 0d)
        {
            return TimeSpan.FromMilliseconds(boundedMs);
        }

        var jitterMs = (_random.NextDouble() * (2d * jitterSpanMs)) - jitterSpanMs;
        var effectiveMs = Math.Max(1d, boundedMs + jitterMs);
        return TimeSpan.FromMilliseconds(effectiveMs);
    }
}
