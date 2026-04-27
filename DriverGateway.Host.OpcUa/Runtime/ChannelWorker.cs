using System.Threading.Channels;
using DriverGateway.Core.Interfaces;
using DriverGateway.Core.Models;
using DriverGateway.Core.Services;
using DriverGateway.Plugins.Abstractions;

namespace DriverGateway.Host.OpcUa.Runtime;

internal sealed class ChannelWorker : IAsyncDisposable
{
    private readonly ChannelDefinition _channel;
    private readonly IReadOnlyDictionary<string, TagDefinition> _tagsByNodeId;
    private readonly IChannelRuntime _runtime;
    private readonly IDemandTracker _demandTracker;
    private readonly ITagValueCache _cache;
    private readonly ExponentialBackoffPolicy _backoffPolicy;
    private readonly Action<string>? _log;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly Channel<QueuedWriteCommand> _writeQueue = Channel.CreateUnbounded<QueuedWriteCommand>();

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private int _failureAttempt;

    public ChannelWorker(
        ChannelDefinition channel,
        IReadOnlyDictionary<string, TagDefinition> tagsByNodeId,
        IChannelRuntime runtime,
        IDemandTracker demandTracker,
        ITagValueCache cache,
        Action<string>? log)
    {
        _channel = channel;
        _tagsByNodeId = tagsByNodeId;
        _runtime = runtime;
        _demandTracker = demandTracker;
        _cache = cache;
        _log = log;
        _backoffPolicy = new ExponentialBackoffPolicy(channel.RetryPolicy);
    }

    public string Name => _channel.Name;

    public void Start()
    {
        if (_loopTask is not null)
        {
            return;
        }

        _loopCts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token));
    }

    public async Task StopAsync()
    {
        if (_loopCts is not null)
        {
            _loopCts.Cancel();
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected on shutdown.
            }
        }

        using var stopCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _runtime.StopAsync(stopCts.Token).ConfigureAwait(false);
    }

    public async Task<WriteResult> WriteImmediateAsync(TagDefinition tag, object? value, CancellationToken cancellationToken)
    {
        if (_runtime.GetConnectionState() != ConnectionState.Connected)
        {
            try
            {
                await TryStartRuntimeAsync(cancellationToken).ConfigureAwait(false);
                _failureAttempt = 0;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _failureAttempt++;
                var backoff = _backoffPolicy.NextDelay(_failureAttempt);
                _log?.Invoke(
                    $"[CHANNEL:{_channel.Name}] Write reconnect failure #{_failureAttempt}. {ex.Message}. Next retry window {backoff.TotalMilliseconds:0} ms.");
                return WriteResult.Fail($"Channel reconnect failed: {ex.Message}");
            }
        }

        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await _runtime.WriteAsync(tag, value, cancellationToken).ConfigureAwait(false);
            if (result.Success)
            {
                _failureAttempt = 0;
                _cache.Upsert(new TagValueSnapshot(tag.NodeIdentifier, value, DateTime.UtcNow, "Good"));
            }

            return result;
        }
        finally
        {
            _ioGate.Release();
        }
    }

    public Task<WriteResult> EnqueueWriteAsync(TagDefinition tag, object? value, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<WriteResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_writeQueue.Writer.TryWrite(new QueuedWriteCommand(tag, value, completion)))
        {
            completion.TrySetResult(WriteResult.Fail("Write queue is closed."));
        }

        return completion.Task;
    }

    public ConnectionState GetConnectionState()
    {
        return _runtime.GetConnectionState();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        await _runtime.DisposeAsync().ConfigureAwait(false);
        _loopCts?.Dispose();
        _ioGate.Dispose();
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        DateTime lastHealthCheckUtc = DateTime.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_runtime.GetConnectionState() != ConnectionState.Connected)
                {
                    await TryStartRuntimeAsync(cancellationToken).ConfigureAwait(false);
                    _failureAttempt = 0;
                }

                var activeDemand = _demandTracker.GetActiveDemand(DateTime.UtcNow);
                var demandedTags = BuildDemandedTags(activeDemand);

                if (demandedTags.Length > 0)
                {
                    await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        var readResults = await _runtime.ReadAsync(demandedTags, cancellationToken).ConfigureAwait(false);
                        foreach (var readResult in readResults)
                        {
                            _cache.Upsert(new TagValueSnapshot(
                                readResult.NodeIdentifier,
                                readResult.Value,
                                readResult.TimestampUtc,
                                readResult.Quality));
                        }
                    }
                    finally
                    {
                        _ioGate.Release();
                    }

                    _failureAttempt = 0;
                    await DrainWriteQueueAsync(cancellationToken).ConfigureAwait(false);

                    var intervalMs = demandedTags
                        .Select(tag => _channel.ResolveScanIntervalMs(tag.ScanClass))
                        .DefaultIfEmpty(250)
                        .Min();
                    await Task.Delay(intervalMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var nowUtc = DateTime.UtcNow;
                var dueForHealthCheck = (nowUtc - lastHealthCheckUtc).TotalMilliseconds >= _channel.RetryPolicy.SafeHealthCheckIntervalMs;
                if (dueForHealthCheck)
                {
                    await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        await _runtime.ReadAsync([], cancellationToken).ConfigureAwait(false);
                        _failureAttempt = 0;
                    }
                    finally
                    {
                        _ioGate.Release();
                    }

                    lastHealthCheckUtc = nowUtc;
                }

                await DrainWriteQueueAsync(cancellationToken).ConfigureAwait(false);
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _failureAttempt++;
                var backoff = _backoffPolicy.NextDelay(_failureAttempt);
                _log?.Invoke($"[CHANNEL:{_channel.Name}] Loop failure #{_failureAttempt}. {ex.Message}. Retry in {backoff.TotalMilliseconds:0} ms.");

                try
                {
                    await TryStartRuntimeAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception startEx)
                {
                    _log?.Invoke(
                        $"[CHANNEL:{_channel.Name}] Reconnect attempt failed after loop failure #{_failureAttempt}: {startEx.Message}");
                }

                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TryStartRuntimeAsync(CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_runtime.GetConnectionState() == ConnectionState.Connected)
            {
                return;
            }

            await _runtime.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task DrainWriteQueueAsync(CancellationToken cancellationToken)
    {
        while (_writeQueue.Reader.TryRead(out var queuedWrite))
        {
            try
            {
                var result = await WriteImmediateAsync(queuedWrite.Tag, queuedWrite.Value, cancellationToken).ConfigureAwait(false);
                queuedWrite.Completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                queuedWrite.Completion.TrySetResult(WriteResult.Fail(ex.Message));
            }
        }
    }

    private TagDefinition[] BuildDemandedTags(IReadOnlySet<string> activeDemand)
    {
        if (activeDemand.Count == 0)
        {
            return [];
        }

        var demandedTags = new List<TagDefinition>(activeDemand.Count);
        foreach (var nodeIdentifier in activeDemand)
        {
            if (_tagsByNodeId.TryGetValue(nodeIdentifier, out var tag))
            {
                demandedTags.Add(tag);
            }
        }

        return demandedTags.ToArray();
    }

    private sealed record QueuedWriteCommand(
        TagDefinition Tag,
        object? Value,
        TaskCompletionSource<WriteResult> Completion);
}
