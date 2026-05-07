using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Channels;

namespace DriverGateway.LoadTests.ModbusTcp;

public enum ModbusOperation
{
    ReadHolding = 1,
    ReadInput = 2,
    WriteSingle = 3,
    WriteMultiple = 4
}

public sealed class LoadTestRunner
{
    private readonly ModbusLoadScenario _scenario;
    private readonly EndpointTarget _target;
    private readonly Channel<string> _events = Channel.CreateUnbounded<string>();
    private readonly List<WorkerHandle> _workers = [];
    private readonly WorkerSharedState _sharedState = new();
    private readonly MetricsCollector _metrics = new();

    private int _nextWorkerId = 1;
    private int _currentTarget;

    public LoadTestRunner(ModbusLoadScenario scenario)
    {
        _scenario = scenario;
        _target = EndpointParser.Parse(scenario.Endpoint);
    }

    public async Task<LoadSummary> RunAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        var eventPump = Task.Run(() => PumpEventsAsync(cancellationToken), cancellationToken);

        try
        {
            foreach (var stage in _scenario.Stages)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"[stage] {stage.Name}: duration={stage.DurationSeconds}s targetClients={stage.TargetClients} interval={stage.RequestIntervalMs}ms ramp={(stage.LinearRamp ? "linear" : "step")}");

                Volatile.Write(ref _sharedState.RequestIntervalMs, stage.RequestIntervalMs);
                await RunStageAsync(stage, cancellationToken).ConfigureAwait(false);
                _currentTarget = stage.TargetClients;
            }
        }
        finally
        {
            await ScaleWorkersAsync(0, cancellationToken).ConfigureAwait(false);
            _events.Writer.TryComplete();
            await eventPump.ConfigureAwait(false);
        }

        var completedAt = DateTime.UtcNow;
        return _metrics.BuildSummary(startedAt, completedAt);
    }

    private async Task RunStageAsync(LoadStage stage, CancellationToken cancellationToken)
    {
        if (!stage.LinearRamp || stage.DurationSeconds == 1)
        {
            await ScaleWorkersAsync(stage.TargetClients, cancellationToken).ConfigureAwait(false);
            await DelayStageAsync(stage.DurationSeconds, cancellationToken).ConfigureAwait(false);
            return;
        }

        var stageDuration = TimeSpan.FromSeconds(stage.DurationSeconds);
        var stageStopwatch = Stopwatch.StartNew();
        while (stageStopwatch.Elapsed < stageDuration && !cancellationToken.IsCancellationRequested)
        {
            var progress = Math.Clamp(stageStopwatch.Elapsed.TotalMilliseconds / stageDuration.TotalMilliseconds, 0.0, 1.0);
            var desired = _currentTarget + (int)Math.Round((stage.TargetClients - _currentTarget) * progress, MidpointRounding.AwayFromZero);
            await ScaleWorkersAsync(desired, cancellationToken).ConfigureAwait(false);

            var sleepMs = Math.Min(stage.ResizeTickMs, Math.Max(20, (int)(stageDuration - stageStopwatch.Elapsed).TotalMilliseconds));
            if (sleepMs <= 0)
            {
                break;
            }

            try
            {
                await Task.Delay(sleepMs, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        await ScaleWorkersAsync(stage.TargetClients, cancellationToken).ConfigureAwait(false);
    }

    private async Task DelayStageAsync(int durationSeconds, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromSeconds(durationSeconds);
        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Controlled stop.
        }
    }

    private async Task ScaleWorkersAsync(int desiredCount, CancellationToken cancellationToken)
    {
        desiredCount = Math.Max(0, desiredCount);
        while (_workers.Count < desiredCount)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var workerId = Interlocked.Increment(ref _nextWorkerId);
            var worker = new VirtualClientWorker(
                workerId,
                _scenario,
                _target,
                _sharedState,
                _metrics,
                _events.Writer);

            var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var task = Task.Run(() => worker.RunAsync(cts.Token), cts.Token);
            _workers.Add(new WorkerHandle(cts, task));
        }

        while (_workers.Count > desiredCount)
        {
            var handle = _workers[^1];
            _workers.RemoveAt(_workers.Count - 1);
            handle.Cancel();
            try
            {
                await handle.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on scale down.
            }
        }
    }

    private async Task PumpEventsAsync(CancellationToken cancellationToken)
    {
        while (await _events.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            while (_events.Reader.TryRead(out var text))
            {
                Console.WriteLine(text);
            }
        }
    }

    private sealed record WorkerHandle(CancellationTokenSource Cancellation, Task Task)
    {
        public void Cancel()
        {
            Cancellation.Cancel();
            Cancellation.Dispose();
        }
    }
}

public sealed class VirtualClientWorker
{
    private readonly int _workerId;
    private readonly ModbusLoadScenario _scenario;
    private readonly EndpointTarget _target;
    private readonly WorkerSharedState _sharedState;
    private readonly MetricsCollector _metrics;
    private readonly ChannelWriter<string> _events;
    private readonly OperationSelector _selector;
    private readonly Random _random;

    private RawModbusClient? _client;
    private int _iteration;

    public VirtualClientWorker(
        int workerId,
        ModbusLoadScenario scenario,
        EndpointTarget target,
        WorkerSharedState sharedState,
        MetricsCollector metrics,
        ChannelWriter<string> events)
    {
        _workerId = workerId;
        _scenario = scenario;
        _target = target;
        _sharedState = sharedState;
        _metrics = metrics;
        _events = events;
        _selector = OperationSelector.FromMix(scenario.Mix);
        _random = new Random(unchecked(scenario.RandomSeed + workerId * 7919));
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            _client = new RawModbusClient(_target.Host, _target.Port, _scenario.UnitId, _scenario.ConnectTimeoutMs);
            while (!cancellationToken.IsCancellationRequested)
            {
                _iteration++;
                var operation = _selector.Next(_random);
                var started = Stopwatch.GetTimestamp();
                var success = false;
                var isTimeout = false;

                try
                {
                    using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    requestCts.CancelAfter(_scenario.RequestTimeoutMs);
                    await ExecuteOperationAsync(operation, requestCts.Token).ConfigureAwait(false);
                    success = true;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    isTimeout = true;
                    await HandleReconnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (IOException)
                {
                    await HandleReconnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                    await HandleReconnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    await HandleReconnectAsync(cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    _metrics.Record(operation, elapsedMs, success, isTimeout);
                }

                var delayMs = CalculateDelayMs();
                if (delayMs > 0)
                {
                    await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            if (_client is not null)
            {
                await _client.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteOperationAsync(ModbusOperation operation, CancellationToken cancellationToken)
    {
        if (_client is null)
        {
            throw new InvalidOperationException("Client is not initialized.");
        }

        var readQuantity = (ushort)_random.Next(_scenario.ReadQuantityMin, _scenario.ReadQuantityMax + 1);
        var readStart = RandomAddress(readQuantity);
        var writeCount = (ushort)_scenario.WriteMultipleQuantity;
        var writeStart = RandomAddress(writeCount);

        switch (operation)
        {
            case ModbusOperation.ReadHolding:
                await _client.ReadHoldingRegistersAsync(readStart, readQuantity, cancellationToken).ConfigureAwait(false);
                break;
            case ModbusOperation.ReadInput:
                await _client.ReadInputRegistersAsync(readStart, readQuantity, cancellationToken).ConfigureAwait(false);
                break;
            case ModbusOperation.WriteSingle:
            {
                var value = (ushort)_random.Next(0, ushort.MaxValue + 1);
                await _client.WriteSingleRegisterAsync(writeStart, value, cancellationToken).ConfigureAwait(false);
                break;
            }
            case ModbusOperation.WriteMultiple:
            {
                var values = new ushort[writeCount];
                for (var index = 0; index < values.Length; index++)
                {
                    values[index] = (ushort)_random.Next(0, ushort.MaxValue + 1);
                }

                await _client.WriteMultipleRegistersAsync(writeStart, values, cancellationToken).ConfigureAwait(false);
                break;
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(operation), operation, "Unsupported operation.");
        }
    }

    private ushort RandomAddress(ushort quantity)
    {
        var maxStart = Math.Max(0, _scenario.AddressSpaceSize - quantity);
        return (ushort)_random.Next(0, maxStart + 1);
    }

    private int CalculateDelayMs()
    {
        var baseDelay = Volatile.Read(ref _sharedState.RequestIntervalMs);
        if (baseDelay <= 0 || _scenario.JitterPercent <= 0)
        {
            return Math.Max(0, baseDelay);
        }

        var jitterBand = (int)Math.Round(baseDelay * (_scenario.JitterPercent / 100.0), MidpointRounding.AwayFromZero);
        if (jitterBand <= 0)
        {
            return baseDelay;
        }

        var jitter = _random.Next(-jitterBand, jitterBand + 1);
        return Math.Max(0, baseDelay + jitter);
    }

    private async Task HandleReconnectAsync(CancellationToken cancellationToken)
    {
        _metrics.RecordReconnect();
        if (_client is not null)
        {
            await _client.DisposeConnectionAsync().ConfigureAwait(false);
        }

        if (_scenario.ReconnectDelayMs > 0)
        {
            await Task.Delay(_scenario.ReconnectDelayMs, cancellationToken).ConfigureAwait(false);
        }

        if (_iteration % 200 == 0)
        {
            _events.TryWrite($"[worker {_workerId}] reconnect count reached {_iteration / 200}");
        }
    }
}

public sealed class WorkerSharedState
{
    public int RequestIntervalMs = 200;
}

public sealed class OperationSelector
{
    private readonly int _thresholdReadHolding;
    private readonly int _thresholdReadInput;
    private readonly int _thresholdWriteSingle;
    private readonly int _thresholdWriteMultiple;

    private OperationSelector(int readHolding, int readInput, int writeSingle, int writeMultiple)
    {
        _thresholdReadHolding = readHolding;
        _thresholdReadInput = readInput;
        _thresholdWriteSingle = writeSingle;
        _thresholdWriteMultiple = writeMultiple;
    }

    public static OperationSelector FromMix(OperationMix mix)
    {
        var readHolding = mix.ReadHoldingWeight;
        var readInput = readHolding + mix.ReadInputWeight;
        var writeSingle = readInput + mix.WriteSingleWeight;
        var writeMultiple = writeSingle + mix.WriteMultipleWeight;
        return new OperationSelector(readHolding, readInput, writeSingle, writeMultiple);
    }

    public ModbusOperation Next(Random random)
    {
        var choice = random.Next(0, _thresholdWriteMultiple);
        if (choice < _thresholdReadHolding)
        {
            return ModbusOperation.ReadHolding;
        }

        if (choice < _thresholdReadInput)
        {
            return ModbusOperation.ReadInput;
        }

        if (choice < _thresholdWriteSingle)
        {
            return ModbusOperation.WriteSingle;
        }

        return ModbusOperation.WriteMultiple;
    }
}

public sealed class MetricsCollector
{
    private long _total;
    private long _success;
    private long _failures;
    private long _timeouts;
    private long _reconnects;

    private readonly ConcurrentDictionary<ModbusOperation, OperationCounters> _operationCounters = new();
    private readonly object _latencyLock = new();
    private readonly List<double> _latenciesMs = [];

    public void Record(ModbusOperation operation, double latencyMs, bool success, bool timeout)
    {
        Interlocked.Increment(ref _total);
        if (success)
        {
            Interlocked.Increment(ref _success);
        }
        else
        {
            Interlocked.Increment(ref _failures);
        }

        if (timeout)
        {
            Interlocked.Increment(ref _timeouts);
        }

        lock (_latencyLock)
        {
            _latenciesMs.Add(latencyMs);
        }

        var counters = _operationCounters.GetOrAdd(operation, _ => new OperationCounters());
        counters.Record(success);
    }

    public void RecordReconnect()
    {
        Interlocked.Increment(ref _reconnects);
    }

    public LoadSummary BuildSummary(DateTime startedAtUtc, DateTime endedAtUtc)
    {
        List<double> latencies;
        lock (_latencyLock)
        {
            latencies = [.. _latenciesMs];
        }

        latencies.Sort();

        var elapsed = endedAtUtc - startedAtUtc;
        var total = Interlocked.Read(ref _total);
        var throughput = elapsed.TotalSeconds <= 0 ? 0 : total / elapsed.TotalSeconds;

        var perOperation = _operationCounters
            .OrderBy(static item => item.Key)
            .ToDictionary(
                static item => item.Key,
                static item => item.Value.Snapshot());

        return new LoadSummary(
            elapsed,
            total,
            Interlocked.Read(ref _success),
            Interlocked.Read(ref _failures),
            Interlocked.Read(ref _timeouts),
            Interlocked.Read(ref _reconnects),
            throughput,
            Percentile(latencies, 50),
            Percentile(latencies, 95),
            Percentile(latencies, 99),
            perOperation);
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, int percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var p = percentile / 100.0;
        var index = Math.Clamp((int)Math.Ceiling(p * sortedValues.Count) - 1, 0, sortedValues.Count - 1);
        return sortedValues[index];
    }
}

public sealed class OperationCounters
{
    private long _total;
    private long _success;
    private long _failures;

    public void Record(bool success)
    {
        Interlocked.Increment(ref _total);
        if (success)
        {
            Interlocked.Increment(ref _success);
        }
        else
        {
            Interlocked.Increment(ref _failures);
        }
    }

    public OperationSummary Snapshot()
    {
        return new OperationSummary(
            Interlocked.Read(ref _total),
            Interlocked.Read(ref _success),
            Interlocked.Read(ref _failures));
    }
}

public sealed record OperationSummary(long Total, long Success, long Failures);

public sealed record LoadSummary(
    TimeSpan Elapsed,
    long TotalRequests,
    long SuccessCount,
    long FailureCount,
    long TimeoutCount,
    long ReconnectCount,
    double RequestsPerSecond,
    double P50Ms,
    double P95Ms,
    double P99Ms,
    IReadOnlyDictionary<ModbusOperation, OperationSummary> PerOperation);
