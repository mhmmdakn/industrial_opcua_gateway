# DriverGateway Plugin Template

Bu şablon yeni bir protocol driver plugin'ini aynı pattern ile eklemek için kullanılır.

## 1. Proje

1. `dotnet new classlib -n DriverGateway.Plugins.<Protocol> -f net8.0 --no-restore`
2. Referansları ekle:
   - `DriverGateway.Core`
   - `DriverGateway.Plugins.Abstractions`
3. Solution'a ekle: `dotnet sln OpcUaSample.sln add ...`

## 2. Minimum Sözleşme

Plugin sınıfı:

```csharp
public sealed class ProtocolDriverPlugin : IDriverPlugin
{
    public string DriverType => "protocol-type";
    public IChannelRuntime CreateChannelRuntime(ChannelRuntimeContext context)
        => new ProtocolChannelRuntime(context);
}
```

Runtime sınıfı:

```csharp
internal sealed class ProtocolChannelRuntime : IChannelRuntime
{
    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    public Task<IReadOnlyCollection<TagReadResult>> ReadAsync(
        IReadOnlyCollection<TagDefinition> demandedTags,
        CancellationToken ct) => Task.FromResult<IReadOnlyCollection<TagReadResult>>([]);
    public Task<WriteResult> WriteAsync(TagDefinition tag, object? value, CancellationToken ct)
        => Task.FromResult(WriteResult.Ok());
    public ConnectionState GetConnectionState() => ConnectionState.Connected;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
```

## 3. Adresleme + Batch

1. Protokole özel `AddressParser` ekle.
2. `IBatchPlanner` implement et.
3. Batch key formatını log/debug için açık tut (`DB1:0-31`, `HoldingRegister:10-30` gibi).

## 4. Doğrulama Checklist

1. `Start/Stop` bağlantı state geçişleri doğru mu?
2. Demand boşken gerçek read request gönderiliyor mu? (Gönderilmemeli)
3. Ardışık adresler tek batch'e birleşiyor mu?
4. `WriteMode.Immediate` ve `WriteMode.Queued` tagleri beklenen davranışı veriyor mu?
5. Plugin DLL startup'ta `plugins` klasöründen yükleniyor mu?
