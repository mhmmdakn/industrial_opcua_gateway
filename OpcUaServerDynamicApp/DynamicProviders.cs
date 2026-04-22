internal interface ITagValueProvider
{
    object GetNextValue(DateTime utcNow);
}

internal static class TagValueProviderFactory
{
    public static ITagValueProvider Create(ProviderConfig providerConfig)
    {
        var providerType = providerConfig.Type.Trim().ToLowerInvariant();

        return providerType switch
        {
            "constant" => new ConstantProvider(providerConfig.Value ?? "0"),
            "counter" => new CounterProvider(
                start: providerConfig.Start ?? 0d,
                step: providerConfig.Step ?? 1d,
                min: providerConfig.Min,
                max: providerConfig.Max,
                wrap: providerConfig.Wrap ?? true),
            "random" => new RandomProvider(
                min: providerConfig.Min ?? 0d,
                max: providerConfig.Max ?? 100d),
            "sine" => new SineProvider(
                amplitude: providerConfig.Amplitude ?? 1d,
                offset: providerConfig.Offset ?? 0d,
                periodSeconds: providerConfig.PeriodSeconds ?? 10d),
            _ => throw new NotSupportedException($"Unknown provider type '{providerConfig.Type}'.")
        };
    }
}

internal sealed class ConstantProvider : ITagValueProvider
{
    private readonly string _rawValue;

    public ConstantProvider(string rawValue)
    {
        _rawValue = rawValue;
    }

    public object GetNextValue(DateTime utcNow)
    {
        return _rawValue;
    }
}

internal sealed class CounterProvider : ITagValueProvider
{
    private readonly double _step;
    private readonly double? _min;
    private readonly double? _max;
    private readonly bool _wrap;
    private double _current;

    public CounterProvider(double start, double step, double? min, double? max, bool wrap)
    {
        _current = start;
        _step = step;
        _min = min;
        _max = max;
        _wrap = wrap;
    }

    public object GetNextValue(DateTime utcNow)
    {
        var valueToReturn = _current;
        _current += _step;

        if (_max.HasValue && _current > _max.Value)
        {
            _current = _wrap ? (_min ?? _max.Value) : _max.Value;
        }

        if (_min.HasValue && _current < _min.Value)
        {
            _current = _wrap ? (_max ?? _min.Value) : _min.Value;
        }

        return valueToReturn;
    }
}

internal sealed class RandomProvider : ITagValueProvider
{
    private readonly Random _random = new();
    private readonly double _min;
    private readonly double _max;

    public RandomProvider(double min, double max)
    {
        if (max < min)
        {
            (min, max) = (max, min);
        }

        _min = min;
        _max = max;
    }

    public object GetNextValue(DateTime utcNow)
    {
        var normalized = _random.NextDouble();
        return _min + ((_max - _min) * normalized);
    }
}

internal sealed class SineProvider : ITagValueProvider
{
    private readonly DateTime _startedAtUtc = DateTime.UtcNow;
    private readonly double _amplitude;
    private readonly double _offset;
    private readonly double _periodSeconds;

    public SineProvider(double amplitude, double offset, double periodSeconds)
    {
        _amplitude = amplitude;
        _offset = offset;
        _periodSeconds = periodSeconds <= 0d ? 10d : periodSeconds;
    }

    public object GetNextValue(DateTime utcNow)
    {
        var elapsed = (utcNow - _startedAtUtc).TotalSeconds;
        var phase = 2d * Math.PI * (elapsed / _periodSeconds);
        return _offset + (_amplitude * Math.Sin(phase));
    }
}
