using DriverGateway.Core.Models;

namespace DriverGateway.Plugins.Abstractions;

public sealed record ChannelRuntimeContext(
    DriverDefinition Driver,
    ChannelDefinition Channel,
    IReadOnlyDictionary<string, TagDefinition> TagsByNodeId,
    Action<string>? Log);

public interface IDriverPlugin
{
    string DriverType { get; }
    IChannelRuntime CreateChannelRuntime(ChannelRuntimeContext context);
}

public interface IChannelRuntime : IAsyncDisposable
{
    Task StartAsync(CancellationToken cancellationToken);
    Task StopAsync(CancellationToken cancellationToken);
    Task<IReadOnlyCollection<TagReadResult>> ReadAsync(
        IReadOnlyCollection<TagDefinition> demandedTags,
        CancellationToken cancellationToken);
    Task<WriteResult> WriteAsync(TagDefinition tag, object? value, CancellationToken cancellationToken);
    ConnectionState GetConnectionState();
}
