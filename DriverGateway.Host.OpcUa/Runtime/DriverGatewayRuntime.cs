using DriverGateway.Core.Interfaces;
using DriverGateway.Core.Models;
using DriverGateway.Core.Services;
using DriverGateway.Plugins.Abstractions;

namespace DriverGateway.Host.OpcUa.Runtime;

internal sealed class DriverGatewayRuntime : IAsyncDisposable
{
    private readonly IReadOnlyDictionary<string, TagDefinition> _tagsByNodeId;
    private readonly IReadOnlyDictionary<string, ChannelWorker> _workersByChannelKey;
    private readonly Dictionary<string, string> _channelKeyByNodeId;
    private readonly IDemandTracker _demandTracker;
    private readonly ITagValueCache _cache;
    private readonly IWritePolicyResolver _writePolicyResolver;
    private readonly Action<string>? _log;

    private DriverGatewayRuntime(
        IReadOnlyDictionary<string, TagDefinition> tagsByNodeId,
        IReadOnlyDictionary<string, ChannelWorker> workersByChannelKey,
        Dictionary<string, string> channelKeyByNodeId,
        IDemandTracker demandTracker,
        ITagValueCache cache,
        IWritePolicyResolver writePolicyResolver,
        Action<string>? log)
    {
        _tagsByNodeId = tagsByNodeId;
        _workersByChannelKey = workersByChannelKey;
        _channelKeyByNodeId = channelKeyByNodeId;
        _demandTracker = demandTracker;
        _cache = cache;
        _writePolicyResolver = writePolicyResolver;
        _log = log;
    }

    public IReadOnlyDictionary<string, TagDefinition> TagsByNodeId => _tagsByNodeId;

    public static DriverGatewayRuntime Build(
        IReadOnlyList<DriverDefinition> drivers,
        IReadOnlyDictionary<string, TagDefinition> tagsByNodeId,
        IReadOnlyDictionary<string, IDriverPlugin> plugins,
        Action<string>? log)
    {
        var demandTracker = new DemandTracker();
        var cache = new InMemoryTagValueCache();
        var writePolicyResolver = new DefaultWritePolicyResolver();

        var workers = new Dictionary<string, ChannelWorker>(StringComparer.OrdinalIgnoreCase);
        var channelKeyByNodeId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var driver in drivers)
        {
            if (!plugins.TryGetValue(driver.DriverType, out var plugin))
            {
                throw new InvalidOperationException($"Driver plugin not found for type '{driver.DriverType}' (driver '{driver.Name}').");
            }

            foreach (var channel in driver.Channels)
            {
                var channelKey = $"{driver.Name}/{channel.Name}";
                var channelTags = channel.Devices
                    .SelectMany(static device => device.Tags)
                    .ToDictionary(static tag => tag.NodeIdentifier, StringComparer.OrdinalIgnoreCase);

                foreach (var tag in channelTags.Values)
                {
                    channelKeyByNodeId[tag.NodeIdentifier] = channelKey;
                }

                var runtimeContext = new ChannelRuntimeContext(driver, channel, channelTags, log);
                var runtime = plugin.CreateChannelRuntime(runtimeContext);
                workers[channelKey] = new ChannelWorker(
                    channel,
                    channelTags,
                    runtime,
                    demandTracker,
                    cache,
                    log);
            }
        }

        return new DriverGatewayRuntime(
            tagsByNodeId,
            workers,
            channelKeyByNodeId,
            demandTracker,
            cache,
            writePolicyResolver,
            log);
    }

    public void Start()
    {
        foreach (var worker in _workersByChannelKey.Values)
        {
            worker.Start();
        }
    }

    public void AddSubscriptionDemand(string nodeIdentifier)
    {
        if (_tagsByNodeId.ContainsKey(nodeIdentifier))
        {
            _demandTracker.AddSubscriptionDemand(nodeIdentifier);
        }
    }

    public void RemoveSubscriptionDemand(string nodeIdentifier)
    {
        _demandTracker.RemoveSubscriptionDemand(nodeIdentifier);
    }

    public void RegisterOneShotDemand(string nodeIdentifier)
    {
        if (_tagsByNodeId.ContainsKey(nodeIdentifier))
        {
            _demandTracker.RegisterOneShotDemand(nodeIdentifier, TimeSpan.FromSeconds(2));
        }
    }

    public bool TryGetCachedValue(string nodeIdentifier, out TagValueSnapshot value)
    {
        return _cache.TryGet(nodeIdentifier, out value!);
    }

    public async Task<WriteResult> WriteAsync(string nodeIdentifier, object? value, CancellationToken cancellationToken)
    {
        if (!_tagsByNodeId.TryGetValue(nodeIdentifier, out var tag))
        {
            return WriteResult.Fail($"Unknown node '{nodeIdentifier}'.");
        }

        if (!_channelKeyByNodeId.TryGetValue(nodeIdentifier, out var channelKey) ||
            !_workersByChannelKey.TryGetValue(channelKey, out var worker))
        {
            return WriteResult.Fail($"No worker mapped for node '{nodeIdentifier}'.");
        }

        var mode = _writePolicyResolver.Resolve(tag);
        return mode switch
        {
            WriteMode.Queued => await worker.EnqueueWriteAsync(tag, value, cancellationToken).ConfigureAwait(false),
            _ => await worker.WriteImmediateAsync(tag, value, cancellationToken).ConfigureAwait(false)
        };
    }

    public IReadOnlyDictionary<string, ConnectionState> SnapshotConnectionStates()
    {
        var output = new Dictionary<string, ConnectionState>(StringComparer.OrdinalIgnoreCase);
        foreach (var (channelKey, worker) in _workersByChannelKey)
        {
            output[channelKey] = worker.GetConnectionState();
        }

        return output;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var worker in _workersByChannelKey.Values)
        {
            try
            {
                await worker.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log?.Invoke($"[RUNTIME] Failed to stop worker '{worker.Name}': {ex.Message}");
            }
        }
    }
}
