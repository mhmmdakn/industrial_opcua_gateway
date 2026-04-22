using DriverGateway.Core.Models;
using DriverGateway.Plugins.Abstractions;

namespace DriverGateway.Plugins.MockSimulator;

public sealed class MockSimulatorDriverPlugin : IDriverPlugin
{
    public const string TypeName = "mock-simulator";

    public string DriverType => TypeName;

    public IChannelRuntime CreateChannelRuntime(ChannelRuntimeContext context)
    {
        return new MockSimulatorChannelRuntime(context);
    }
}

internal sealed class MockSimulatorChannelRuntime : IChannelRuntime
{
    private readonly ChannelRuntimeContext _context;
    private readonly Dictionary<string, object?> _memory = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new();
    private readonly object _syncRoot = new();

    private ConnectionState _connectionState = ConnectionState.Disconnected;

    public MockSimulatorChannelRuntime(ChannelRuntimeContext context)
    {
        _context = context;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            _connectionState = ConnectionState.Connecting;
            SeedTagMemory();
            _connectionState = ConnectionState.Connected;
        }

        _context.Log?.Invoke($"[MOCK] Channel '{_context.Channel.Name}' connected.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            _connectionState = ConnectionState.Disconnected;
        }

        _context.Log?.Invoke($"[MOCK] Channel '{_context.Channel.Name}' disconnected.");
        return Task.CompletedTask;
    }

    public Task<IReadOnlyCollection<TagReadResult>> ReadAsync(
        IReadOnlyCollection<TagDefinition> demandedTags,
        CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (_connectionState != ConnectionState.Connected)
            {
                throw new InvalidOperationException("Mock simulator channel is not connected.");
            }

            // Read(empty) is used as health-check by the scheduler.
            if (demandedTags.Count == 0)
            {
                MaybeInjectTransientDisconnect();
                return Task.FromResult<IReadOnlyCollection<TagReadResult>>([]);
            }

            var now = DateTime.UtcNow;
            var results = new List<TagReadResult>(demandedTags.Count);
            foreach (var tag in demandedTags)
            {
                if (!_memory.TryGetValue(tag.NodeIdentifier, out var currentValue))
                {
                    currentValue = CreateSeedValue(tag);
                }

                var nextValue = AdvanceValue(tag, currentValue);
                _memory[tag.NodeIdentifier] = nextValue;
                results.Add(new TagReadResult(tag.NodeIdentifier, nextValue, now));
            }

            MaybeInjectTransientDisconnect();
            return Task.FromResult<IReadOnlyCollection<TagReadResult>>(results);
        }
    }

    public Task<WriteResult> WriteAsync(TagDefinition tag, object? value, CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (_connectionState != ConnectionState.Connected)
            {
                return Task.FromResult(WriteResult.Fail("Mock simulator channel is disconnected."));
            }

            try
            {
                _memory[tag.NodeIdentifier] = ValueConversion.Convert(value, tag.DataType);
                return Task.FromResult(WriteResult.Ok());
            }
            catch (Exception ex)
            {
                return Task.FromResult(WriteResult.Fail(ex.Message));
            }
        }
    }

    public ConnectionState GetConnectionState()
    {
        lock (_syncRoot)
        {
            return _connectionState;
        }
    }

    public ValueTask DisposeAsync()
    {
        return ValueTask.CompletedTask;
    }

    private void SeedTagMemory()
    {
        foreach (var tag in _context.TagsByNodeId.Values)
        {
            _memory[tag.NodeIdentifier] = CreateSeedValue(tag);
        }
    }

    private object? CreateSeedValue(TagDefinition tag)
    {
        return tag.DataType switch
        {
            TagDataType.Double => Math.Round(20d + (_random.NextDouble() * 10d), 2),
            TagDataType.Float => (float)Math.Round(20d + (_random.NextDouble() * 10d), 2),
            TagDataType.Boolean => _random.Next(0, 2) == 1,
            TagDataType.Int16 => (short)_random.Next(short.MinValue, short.MaxValue),
            TagDataType.UInt16 => (ushort)_random.Next(0, ushort.MaxValue + 1),
            TagDataType.Word => (ushort)_random.Next(0, ushort.MaxValue + 1),
            TagDataType.Int32 => _random.Next(0, 100),
            TagDataType.UInt32 => (uint)_random.NextInt64(0, int.MaxValue),
            TagDataType.DWord => (uint)_random.NextInt64(0, int.MaxValue),
            TagDataType.String => "RUNNING",
            _ => 0d
        };
    }

    private object? AdvanceValue(TagDefinition tag, object? currentValue)
    {
        return tag.DataType switch
        {
            TagDataType.Double => Math.Round(Convert.ToDouble(currentValue) + ((_random.NextDouble() - 0.5d) * 0.75d), 3),
            TagDataType.Float => (float)(Convert.ToSingle(currentValue) + ((_random.NextDouble() - 0.5d) * 0.75d)),
            TagDataType.Boolean => _random.NextDouble() >= 0.92d ? !Convert.ToBoolean(currentValue) : Convert.ToBoolean(currentValue),
            TagDataType.Int16 => unchecked((short)(Convert.ToInt16(currentValue) + 1)),
            TagDataType.UInt16 => unchecked((ushort)(Convert.ToUInt16(currentValue) + 1)),
            TagDataType.Word => unchecked((ushort)(Convert.ToUInt16(currentValue) + 1)),
            TagDataType.Int32 => Convert.ToInt32(currentValue) + 1,
            TagDataType.UInt32 => unchecked(Convert.ToUInt32(currentValue) + 1),
            TagDataType.DWord => unchecked(Convert.ToUInt32(currentValue) + 1),
            TagDataType.String => $"RUNNING-{DateTime.UtcNow:HHmmss}",
            _ => currentValue
        };
    }

    private void MaybeInjectTransientDisconnect()
    {
        if (!_context.Channel.Settings.TryGetValue("disconnectRate", out var rawRate) ||
            !double.TryParse(rawRate, out var disconnectRate) ||
            disconnectRate <= 0d)
        {
            return;
        }

        var roll = _random.NextDouble();
        if (roll > disconnectRate)
        {
            return;
        }

        _connectionState = ConnectionState.Faulted;
        throw new InvalidOperationException("Mock simulator injected a transient disconnect.");
    }
}

internal static class ValueConversion
{
    public static object Convert(object? rawValue, TagDataType dataType)
    {
        return dataType switch
        {
            TagDataType.Double => System.Convert.ToDouble(rawValue),
            TagDataType.Float => System.Convert.ToSingle(rawValue),
            TagDataType.Boolean => rawValue is bool boolValue
                ? boolValue
                : bool.Parse(rawValue?.ToString() ?? "false"),
            TagDataType.Int16 => System.Convert.ToInt16(rawValue),
            TagDataType.UInt16 => System.Convert.ToUInt16(rawValue),
            TagDataType.Word => System.Convert.ToUInt16(rawValue),
            TagDataType.Int32 => System.Convert.ToInt32(rawValue),
            TagDataType.UInt32 => System.Convert.ToUInt32(rawValue),
            TagDataType.DWord => System.Convert.ToUInt32(rawValue),
            TagDataType.String => rawValue?.ToString() ?? string.Empty,
            _ => throw new NotSupportedException($"Unsupported data type: {dataType}")
        };
    }
}
